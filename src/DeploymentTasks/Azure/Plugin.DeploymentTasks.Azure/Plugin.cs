﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Certify.Models.Config;
using Certify.Models.Plugins;
using Certify.Providers.DeploymentTasks;

namespace Plugin.DeploymentTasks.Azure
{
    public class Plugin : PluginProviderBase<IDeploymentTaskProvider, DeploymentProviderDefinition>, IDeploymentTaskProviderPlugin
    { }

}