﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Certify.Config;
using Certify.Management;
using Certify.Models;
using Certify.Models.Config;
using Certify.Models.Providers;
using Plugin.DeploymentTasks.Shared;

namespace Certify.Providers.DeploymentTasks
{
    public class RemoteAccess : IDeploymentTaskProvider
    {
        public static new DeploymentProviderDefinition Definition { get; }
        public new DeploymentProviderDefinition GetDefinition(DeploymentProviderDefinition currentDefinition) => (currentDefinition ?? Definition);

        private const string SCRIPT_NAME = "RemoteAccess.ps1";

        public async Task<List<ActionResult>> Execute(ILog log, object subject, DeploymentTaskConfig settings, Dictionary<string, string> credentials, bool isPreviewOnly, DeploymentProviderDefinition definition)
        {

            var validation = await Validate(subject, settings, credentials, definition);

            if (validation.Any())
            {
                return validation;
            }

            var script = Helpers.ReadStringResource(SCRIPT_NAME);

            definition = GetDefinition(definition);

            var certRequest = subject as CertificateRequestResult;

            log?.Information("Executing command via PowerShell");

            var parameters = new Dictionary<string, object>();

            var scriptResult = await PowerShellManager.RunScript(certRequest, parameters: parameters, scriptContent: script, credentials: credentials);

            return new List<ActionResult> { scriptResult };

        }

        public async Task<List<ActionResult>> Validate(object subject, DeploymentTaskConfig settings, Dictionary<string, string> credentials, DeploymentProviderDefinition definition)
        {
            var results = new List<ActionResult>();

            return results;
        }

        static RemoteAccess()
        {
            Definition = new DeploymentProviderDefinition
            {
                Id = "Certify.Providers.DeploymentTasks.RemoteAccess",
                Title = "Deploy to RAS (DirectAccess, VPN, SSTP VPN etc)",
                DefaultTitle = "Deploy to Remote Access Services",
                IsExperimental = false,
                UsageType = DeploymentProviderUsage.PostRequest,
                SupportedContexts = DeploymentContextType.LocalAsService | DeploymentContextType.LocalAsUser,
                Description = "Deploy latest certificate to RAS using Powershell (Set-RemoteAccess)",
                ProviderParameters = new System.Collections.Generic.List<ProviderParameter>
                {
                    new ProviderParameter { Key = "restartServices", Name = "Include Service Restart?", Type= OptionType.Boolean, IsCredential = false, Value="false" },
                }
            };
        }

    }
}
