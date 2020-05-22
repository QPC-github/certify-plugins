﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Certify.Models.Config;
using Certify.Models.Plugins;
using Certify.Providers.CertificateManagers;
using Certify.Providers.DeploymentTasks;

namespace Plugin.CertificateManagers
{
    public class Plugin : PluginProviderBase<ICertificateManager, ProviderDefinition>, ICertificateManagerProviderPlugin
    {
    }
}
