﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Runtime.Plugins;

namespace Microsoft.BotFramework.Composer.CustomAction
{
    public class MSGraphCustomActionPlugin : IBotPlugin
    {
        public void Load(IBotPluginLoadContext context)
        {
            ComponentRegistration.Add(new MSGraphComponentRegistration());
        }
    }
}
