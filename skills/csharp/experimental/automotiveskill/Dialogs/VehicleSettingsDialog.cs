﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutomotiveSkill.Models;
using AutomotiveSkill.Models.Actions;
using AutomotiveSkill.Responses.VehicleSettings;
using AutomotiveSkill.Utilities;
using Luis;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Dialogs.Choices;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Solutions.Responses;
using Microsoft.Recognizers.Text;
using SkillServiceLibrary.Utilities;

namespace AutomotiveSkill.Dialogs
{
    public class VehicleSettingsDialog : AutomotiveSkillDialogBase
    {
        private const string FallbackSettingImageFileName = "Black_Car.png";
        private const string AvailableSettingsFileName = "available_settings.yaml";
        private const string AlternativeSettingsFileName = "setting_alternative_names.yaml";

        // Unused now. same as SettingChoice.json
        private const string AdaptiveCardBackgroundSVG = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAKwAAACeCAYAAACvg+F+AAAABGdBTUEAALGPC/xhBQAAAAFzUkdCAK7OHOkAAAAJcEhZcwAAFiUAABYlAUlSJPAAAAAhdEVYdENyZWF0aW9uIFRpbWUAMjAxOTowMzoxMyAxOTo0Mjo0OBCBEeIAAAG8SURBVHhe7dJBDQAgEMCwA/+egQcmlrSfGdg6z0DE/oUEw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phSTEsKYYlxbCkGJYUw5JiWFIMS4phCZm52U4FOCAVGHQAAAAASUVORK5CYII=";

        private static readonly Regex WordCharacter = new Regex("^\\w", RegexOptions.Compiled);
        private static readonly IReadOnlyDictionary<string, string> SettingValueToSpeakableIngForm = new Dictionary<string, string>
        {
            { "decrease", "Decreasing" },
            { "increase", "Increasing" },
        };

        private readonly IRecognizer vehicleSettingNameSelectionLuisRecognizer;
        private readonly IRecognizer vehicleSettingValueSelectionLuisRecognizer;

        private readonly SettingList settingList;
        private readonly SettingFilter settingFilter;

        private IHttpContextAccessor _httpContext;

        public VehicleSettingsDialog(
            IServiceProvider serviceProvider,
            IHttpContextAccessor httpContext)
            : base(nameof(VehicleSettingsDialog), serviceProvider)
        {
            var localeConfig = Services.GetCognitiveModels();

            // Initialise supporting LUIS models for followup questions
            vehicleSettingNameSelectionLuisRecognizer = localeConfig.LuisServices["SettingsName"];
            vehicleSettingValueSelectionLuisRecognizer = localeConfig.LuisServices["SettingsValue"];

            // Supporting setting files are stored as embedded resources
            var resourceAssembly = typeof(VehicleSettingsDialog).Assembly;

            var settingFile = resourceAssembly
                .GetManifestResourceNames()
                .Where(x => x.Contains(AvailableSettingsFileName))
                .First();

            var alternativeSettingFileName = resourceAssembly
                .GetManifestResourceNames()
                .Where(x => x.Contains(AlternativeSettingsFileName))
                .First();

            if (string.IsNullOrEmpty(settingFile) || string.IsNullOrEmpty(alternativeSettingFileName))
            {
                throw new FileNotFoundException($"Unable to find Available Setting and/or Alternative Names files in \"{resourceAssembly.FullName}\" assembly.");
            }

            settingList = new SettingList(resourceAssembly, settingFile, alternativeSettingFileName);
            settingFilter = new SettingFilter(settingList);

            // Setting Change waterfall
            var processVehicleSettingChangeWaterfall = new WaterfallStep[]
            {
                ProcessSettingAsync,
                ProcessVehicleSettingsChangeAsync,
                ProcessChangeAsync,
                SendChangeAsync
            };
            AddDialog(new WaterfallDialog(Actions.ProcessVehicleSettingChange, processVehicleSettingChangeWaterfall));

            // Prompts
            AddDialog(new ChoicePrompt(Actions.SettingNameSelectionPrompt, SettingNameSelectionValidatorAsync, Culture.English) { Style = ListStyle.Auto, ChoiceOptions = new ChoiceFactoryOptions { InlineSeparator = string.Empty, InlineOr = string.Empty, InlineOrMore = string.Empty, IncludeNumbers = true } });
            AddDialog(new ChoicePrompt(Actions.SettingValueSelectionPrompt, SettingValueSelectionValidatorAsync, Culture.English) { Style = ListStyle.Auto, ChoiceOptions = new ChoiceFactoryOptions { InlineSeparator = string.Empty, InlineOr = string.Empty, InlineOrMore = string.Empty, IncludeNumbers = true } });

            AddDialog(new ConfirmPrompt(Actions.SettingConfirmationPrompt));

            // Set starting dialog for component
            InitialDialogId = Actions.ProcessVehicleSettingChange;

            // Used to resolve image paths (local or hosted)
            _httpContext = httpContext;
        }

        /// <summary>
        /// Top level processing, is the user trying to check or change a setting?.
        /// </summary>
        /// <param name="sc">Step Context.</param>
        /// <param name="cancellationToken">Cancellation Token.</param>
        /// <returns>Dialog Turn Result.</returns>
        public async Task<DialogTurnResult> ProcessSettingAsync(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await Accessor.GetAsync(sc.Context, () => new AutomotiveSkillState(), cancellationToken);
            var skillResult = sc.Context.TurnState.Get<SettingsLuis>(StateProperties.SettingsLuisResultKey);
            bool isDeclarative = skillResult?.TopIntent().intent == SettingsLuis.Intent.VEHICLE_SETTINGS_DECLARATIVE;

            // Perform post-processing on the entities, if it's declarative we indicate for special processing (opposite of the condition they've expressed)
            settingFilter.PostProcessSettingName(state, isDeclarative);

            // Perform content logic and remove entities that don't make sense
            settingFilter.ApplyContentLogic(state);

            var settingNames = state.GetUniqueSettingNames();
            if (!settingNames.Any())
            {
                // missing setting name
                await sc.Context.SendActivityAsync(TemplateManager.GenerateActivityForLocale(VehicleSettingsResponses.VehicleSettingsMissingSettingName), cancellationToken);
                return await sc.EndDialogAsync(cancellationToken: cancellationToken);
            }
            else if (settingNames.Count() > 1)
            {
                // If we have more than one setting name matching prompt the user to choose
                var options = new PromptOptions()
                {
                    Choices = new List<Choice>(),
                };

                for (var i = 0; i < settingNames.Count; ++i)
                {
                    var item = settingNames[i];
                    var synonyms = new List<string>
                    {
                        item,
                        (i + 1).ToString()
                    };
                    synonyms.AddRange(settingList.GetAlternativeNamesForSetting(item));
                    var choice = new Choice()
                    {
                        Value = item,
                        Synonyms = synonyms,
                    };
                    options.Choices.Add(choice);
                }

                var cardModel = new AutomotiveCardModel()
                {
                    ImageUrl = GetSettingCardImageUri(FallbackSettingImageFileName)
                };

                var card = TemplateManager.GenerateActivityForLocale(GetDivergedCardName(sc.Context, "AutomotiveCard"), cardModel);
                options.Prompt = TemplateManager.GenerateActivityForLocale(VehicleSettingsResponses.VehicleSettingsSettingNameSelection);
                options.Prompt.Attachments = card.Attachments;

                // Default Text property is clumsy for speech
                options.Prompt.Speak = SpeechUtility.ListToSpeechReadyString(options);

                // Workaround. In teams, prompt will be changed to HeroCard and adaptive card could not be shown. So send them separatly
                if (Channel.GetChannelId(sc.Context) == Channels.Msteams)
                {
                    await sc.Context.SendActivityAsync(options.Prompt, cancellationToken);
                    options.Prompt = null;
                }

                return await sc.PromptAsync(Actions.SettingNameSelectionPrompt, options, cancellationToken);
            }
            else
            {
                // Only one setting detected so move on to next stage
                return await sc.NextAsync(cancellationToken: cancellationToken);
            }
        }

        /// <summary>
        /// When we've had to prompt the user to clarify the setting name we need to validate the input and run the pre-processing again.
        /// </summary>
        /// <param name="promptContext">Prompt context to validate.</param>
        /// <param name="cancellationToken">Cancellation Token.</param>
        /// <returns>Whether setting was validated.</returns>
        private async Task<bool> SettingNameSelectionValidatorAsync(PromptValidatorContext<FoundChoice> promptContext, CancellationToken cancellationToken)
        {
            var state = await Accessor.GetAsync(promptContext.Context, cancellationToken: cancellationToken);

            // Use the name selection LUIS model to perform validation of the user's entered setting name
            var nameSelectionResult = await vehicleSettingNameSelectionLuisRecognizer.RecognizeAsync<SettingsNameLuis>(promptContext.Context, cancellationToken);
            state.AddRecognizerResult(nameSelectionResult);

            var selectedSettingNames = new List<string>();
            if (nameSelectionResult.Entities.SETTING != null)
            {
                selectedSettingNames.AddRange(nameSelectionResult.Entities.SETTING);
            }
            else if (promptContext.Recognized.Value != null && promptContext.Recognized.Value.Value != null)
            {
                selectedSettingNames.Add(promptContext.Recognized.Value.Value);
            }

            if (selectedSettingNames.Any())
            {
                var selectedChanges = settingFilter.ApplySelectionToSettings(state, selectedSettingNames, state.Changes);

                if (selectedChanges != null)
                {
                    state.Changes = selectedChanges;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Once we have a setting we need to process the corresponding value.
        /// </summary>
        /// <param name="sc">Step Context.</param>
        /// <returns>Dialog Turn Result.</returns>
        private async Task<DialogTurnResult> ProcessVehicleSettingsChangeAsync(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);

            if (state.Changes.Any())
            {
                var settingValues = state.GetUniqueSettingValues();
                if (!settingValues.Any())
                {
                    // This shouldn't happen because the SettingFilter would just add all possible values to let the user select from them.
                    await sc.Context.SendActivityAsync(TemplateManager.GenerateActivityForLocale(VehicleSettingsResponses.VehicleSettingsMissingSettingValue), cancellationToken);
                    return await sc.EndDialogAsync(cancellationToken: cancellationToken);
                }
                else
                {
                    // We have found multiple setting values, which we need to prompt the user to resolve
                    if (settingValues.Count() > 1)
                    {
                        var settingName = state.Changes.First().SettingName;
                        var setting = this.settingList.FindSetting(settingName);

                        // If an image filename is provided we'll use it otherwise fall back to the generic car one
                        var imageName = setting.ImageFileName ?? FallbackSettingImageFileName;

                        // If we have more than one setting value matching, prompt the user to choose
                        var options = new PromptOptions()
                        {
                            Choices = new List<Choice>(),
                        };

                        for (var i = 0; i < settingValues.Count; ++i)
                        {
                            var item = settingValues[i];
                            var synonyms = new List<string>
                            {
                                item,
                                (i + 1).ToString()
                            };
                            synonyms.AddRange(settingList.GetAlternativeNamesForSettingValue(settingName, item));
                            var choice = new Choice()
                            {
                                Value = item,
                                Synonyms = synonyms,
                            };
                            options.Choices.Add(choice);
                        }

                        var promptReplacements = new { settingName };
                        var cardModel = new AutomotiveCardModel()
                        {
                            ImageUrl = GetSettingCardImageUri(imageName)
                        };

                        var card = TemplateManager.GenerateActivityForLocale(GetDivergedCardName(sc.Context, "AutomotiveCard"), cardModel);
                        options.Prompt = TemplateManager.GenerateActivityForLocale(VehicleSettingsResponses.VehicleSettingsSettingValueSelection, promptReplacements);
                        options.Prompt.Attachments = card.Attachments;

                        // Default Text property is clumsy for speech
                        options.Prompt.Speak = SpeechUtility.ListToSpeechReadyString(options.Prompt);

                        // Workaround. In teams, prompt will be changed to HeroCard and adaptive card could not be shown. So send them separatly
                        if (Channel.GetChannelId(sc.Context) == Channels.Msteams)
                        {
                            await sc.Context.SendActivityAsync(options.Prompt, cancellationToken);
                            options.Prompt = null;
                        }

                        return await sc.PromptAsync(Actions.SettingValueSelectionPrompt, options, cancellationToken);
                    }
                    else
                    {
                        // We only have one setting value so proceed to next step
                        return await sc.NextAsync(cancellationToken: cancellationToken);
                    }
                }
            }
            else
            {
                // No setting value was understood
                await sc.Context.SendActivityAsync(TemplateManager.GenerateActivityForLocale(VehicleSettingsResponses.VehicleSettingsOutOfDomain), cancellationToken);
                return await sc.EndDialogAsync(cancellationToken: cancellationToken);
            }
        }

        /// <summary>
        /// Take the users input for setting value selection and validate it matches the chosen setting value - e.g. Off for Parking Assistance.
        /// </summary>
        /// <param name="promptContext">Prompt Context.</param>
        /// <param name="cancellationToken">Cancellation Token.</param>
        /// <returns>Whether prompt value was validated.</returns>
        private async Task<bool> SettingValueSelectionValidatorAsync(PromptValidatorContext<FoundChoice> promptContext, CancellationToken cancellationToken)
        {
            var state = await Accessor.GetAsync(promptContext.Context, cancellationToken: cancellationToken);

            // Use the value selection LUIS model to perform validation of the users entered setting value
            var valueSelectionResult = await vehicleSettingValueSelectionLuisRecognizer.RecognizeAsync<SettingsValueLuis>(promptContext.Context, CancellationToken.None);
            state.AddRecognizerResult(valueSelectionResult);

            var valueEntities = new List<string>();
            if (valueSelectionResult.Entities.VALUE != null)
            {
                valueEntities.AddRange(valueSelectionResult.Entities.VALUE);
            }
            else if (valueSelectionResult.Entities.SETTING != null)
            {
                valueEntities.AddRange(valueSelectionResult.Entities.SETTING);
            }
            else if (promptContext.Recognized.Value != null && promptContext.Recognized.Value.Value != null)
            {
                valueEntities.Add(promptContext.Recognized.Value.Value);
            }

            if (valueEntities.Any())
            {
                var selectedChanges = settingFilter.ApplySelectionToSettingValues(state, valueEntities);

                // We identified a setting value, proceed
                if (selectedChanges != null)
                {
                    state.Changes = selectedChanges;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Process the change that we are about to perform. If required the user is prompted for confirmation.
        /// </summary>
        /// <param name="sc">Step Context.</param>
        /// <param name="cancellationToken">Cancellation Token.</param>
        /// <returns>Dialog Turn Result.</returns>
        private async Task<DialogTurnResult> ProcessChangeAsync(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);

            // Perform the request change
            if (state.Changes.Any())
            {
                var change = state.Changes[0];
                if (change.OperationStatus == SettingOperationStatus.TO_DO)
                {
                    // TODO - Validation of change would go here, for now we just apply the change
                    var availableSetting = this.settingList.FindSetting(change.SettingName);
                    var availableSettingValue = this.settingList.FindSettingValue(availableSetting, change.Value);

                    // Check confirmation first.
                    if (availableSettingValue != null && availableSettingValue.RequiresConfirmation)
                    {
                        var promptTemplate = VehicleSettingsResponses.VehicleSettingsSettingChangeConfirmation;
                        var promptReplacements = new
                        {
                            settingName = change.SettingName,
                            value = change.Value
                        };

                        // TODO - Explore moving to ConfirmPrompt following usability testing
                        var prompt = TemplateManager.GenerateActivityForLocale(promptTemplate, promptReplacements);
                        return await sc.PromptAsync(Actions.SettingConfirmationPrompt, new PromptOptions { Prompt = prompt }, cancellationToken);
                    }
                    else
                    {
                        // No confirmation required so we skip to sending the change
                        return await sc.NextAsync(cancellationToken: cancellationToken);
                    }
                }
                else
                {
                    await sc.Context.SendActivityAsync(TemplateManager.GenerateActivityForLocale(VehicleSettingsResponses.VehicleSettingsSettingChangeUnsupported), cancellationToken);
                    return await sc.EndDialogAsync(cancellationToken: cancellationToken);
                }
            }
            else
            {
                await sc.Context.SendActivityAsync(TemplateManager.GenerateActivityForLocale(VehicleSettingsResponses.VehicleSettingsSettingChangeUnsupported), cancellationToken);
                return await sc.EndDialogAsync(cancellationToken: cancellationToken);
            }
        }

        private async Task<DialogTurnResult> SendChangeAsync(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);

            var change = state.Changes[0];
            var settingChangeConfirmed = false;

            // If we skip the ConfirmPrompt due to no confirmation needed then Result will be NULL
            if (sc.Result == null)
            {
                settingChangeConfirmed = true;
            }
            else
            {
                settingChangeConfirmed = (bool)sc.Result;
                change.IsConfirmed = settingChangeConfirmed;
            }

            if (settingChangeConfirmed)
            {
                    string promptTemplate = VehicleSettingsResponses.VehicleSettingsConfirmed;

                    await SendActionToDeviceAsync(sc, change, cancellationToken);
                    await sc.Context.SendActivityAsync(TemplateManager.GenerateActivityForLocale(promptTemplate), cancellationToken);
            }
            else
            {
                await sc.Context.SendActivityAsync(TemplateManager.GenerateActivityForLocale(VehicleSettingsResponses.VehicleSettingsSettingChangeConfirmationDenied), cancellationToken);
            }

            if (state.IsAction)
            {
                return await sc.EndDialogAsync(new ActionResult(true), cancellationToken);
            }

            return await sc.EndDialogAsync(cancellationToken: cancellationToken);
        }

        private string UnitToString(string unit)
        {
            if (unit != null && WordCharacter.Match(unit).Success)
            {
                return $" {unit}";
            }
            else
            {
                return unit;
            }
        }

        /// <summary>
        /// Send an event activity to communicate to the client which change to make to the actual setting.
        /// This event is meant to be processed by client code rather than shown to the user.
        /// </summary>
        /// <param name="sc">The WaterfallStepContext.</param>
        /// <param name="change">The change that we want the client to make.</param>
        /// <returns>A Task.</returns>
        private async Task SendActionToDeviceAsync(WaterfallStepContext sc, SettingChange change, CancellationToken cancellationToken)
        {
            // workaround. if connect skill directly to teams, the following reply does not work.
            if (!sc.Context.IsSkill() && Channel.GetChannelId(sc.Context) == Channels.Msteams)
            {
                return;
            }

            var actionEvent = sc.Context.Activity.CreateReply();
            actionEvent.Type = ActivityTypes.Event;

            // The name of the event is the intent (changing vs checking, the latter of which is not yet supported).
            actionEvent.Name = $"AutomotiveSkill.{change.SettingName}";
            actionEvent.Value = change;

            await sc.Context.SendActivityAsync(actionEvent, cancellationToken);
        }

        private string GetSettingCardImageUri(string imagePath)
        {
            var serverUrl = _httpContext.HttpContext.Request.Scheme + "://" + _httpContext.HttpContext.Request.Host.Value;
            return $"{serverUrl}/images/{imagePath}";
        }

        private string GetSpeakableOptions(IList<Choice> choices)
        {
            IList<string> speakableChoices = new List<string>();
            for (var i = 0; i < choices.Count(); ++i)
            {
                // The dash makes the voice take a short break, which is what a human would do when reading out a numbered list.
                speakableChoices.Add($"{(i + 1).ToString()} - {choices[i].Value.ToString()}.");
            }

            return string.Join(", ", speakableChoices);
        }
    }
}