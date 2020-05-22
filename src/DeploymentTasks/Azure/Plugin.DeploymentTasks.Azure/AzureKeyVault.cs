﻿using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using Certify.Config;
using Certify.Models;
using Certify.Models.Config;
using Certify.Models.Providers;
using Certify.Providers.DeploymentTasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Plugin.DeploymentTasks.Azure
{

    public class AzureKeyVault : IDeploymentTaskProvider
    {
        public static DeploymentProviderDefinition Definition { get; }
        public DeploymentProviderDefinition GetDefinition(DeploymentProviderDefinition currentDefinition = null) => (currentDefinition ?? Definition);

        private IdnMapping _idnMapping = new IdnMapping();

        static AzureKeyVault()
        {
            Definition = new DeploymentProviderDefinition
            {
                Id = "Certify.Providers.DeploymentTasks.AzureKeyVault",
                Title = "Deploy to Azure Key Vault",
                IsExperimental = true,
                UsageType = DeploymentProviderUsage.Any,
                SupportedContexts = DeploymentContextType.ExternalCredential,
                ExternalCredentialType = "ExternalAuth.Azure.ClientSecret",
                Description = "Store a certificate in a Microsoft Azure Key Vault",
                ProviderParameters = new List<ProviderParameter>
                {
                    new ProviderParameter{ Key="vault_uri", Name="Azure Vault Uri", IsRequired=true, IsCredential=false,  Description="e.g. https://<vault-name>.vault.azure.net/", Type= OptionType.String },
                    new ProviderParameter{ Key="cert_name", Name="Certificate Name", IsRequired=false, IsCredential=false,  Description="(optional, alphanumeric characters 0-9a-Z or -)", Type= OptionType.String }
                }
            };
        }

        /// <summary>
        /// Deploy current cert to Azure Key Vault
        /// </summary>
        /// <param name="log"></param>
        /// <param name="managedCert"></param>
        /// <param name="settings"></param>
        /// <param name="credentials"></param>
        /// <param name="isPreviewOnly"></param>
        /// <returns></returns>
        public async Task<List<ActionResult>> Execute(
          ILog log,
          object subject,
          DeploymentTaskConfig settings,
          Dictionary<string, string> credentials,
          bool isPreviewOnly,
          DeploymentProviderDefinition definition,
          CancellationToken cancellationToken
          )
        {

            definition = GetDefinition(definition);

            var results = await Validate(subject, settings, credentials, definition);

            if (results.Any())
            {
                return results;
            }

            var managedCert = ManagedCertificate.GetManagedCertificate(subject);

            if (string.IsNullOrEmpty(managedCert.CertificatePath))
            {
                results.Add(new ActionResult("No certificate to deploy.", false));
                return results;
            }

            var keyVaultUri = new Uri(settings.Parameters.FirstOrDefault(c => c.Key == "vault_uri")?.Value);

            var pfxData = File.ReadAllBytes(managedCert.CertificatePath);

            // from application user details in Azure AD

            var cred = new ClientSecretCredential(credentials["tenantid"], credentials["clientid"], credentials["secret"]);

            var client = new CertificateClient(keyVaultUri, cred);

            var customName = settings.Parameters.FirstOrDefault(c => c.Key == "cert_name")?.Value;

            var certName = GetStringAsKeyVaultName(customName ?? managedCert.Name);

            var importOptions = new ImportCertificateOptions(certName, pfxData);

            try
            {
                await client.ImportCertificateAsync(importOptions);

                log.Information($"Deployed certificate [{certName}] to Azure Key Vault");

                results.Add(new ActionResult("Certificate Deployed to Azure Key Vault", true));
            }
            catch (AuthenticationFailedException exp)
            {
                log.Error($"Azure Authentiation error: {exp.InnerException?.Message ?? exp.Message}");
                results.Add(new ActionResult("Key Vault Deployment Failed", false));
            }
            catch (Exception exp)
            {
                log.Error($"Failed to deploy certificate [{certName}] to Azure Key Vault :{exp}");
                results.Add(new ActionResult("Key Vault Deployment Failed", false));
            }

            return results;
        }

        /// <summary>
        /// prepare version of string that is compatible with azure keyvault names
        /// https://docs.microsoft.com/en-us/rest/api/keyvault/ImportCertificate/ImportCertificate
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        private string GetStringAsKeyVaultName(string name)
        {
            if (name == null) return null;

            var ascii = _idnMapping.GetAscii(name);

            // ^[0-9a-zA-Z-]+$ : alphanumeric or '-'
            ascii = ascii.Replace(".", "-");

            return ascii;
        }

        public async Task<List<ActionResult>> Validate(object subject, DeploymentTaskConfig settings, Dictionary<string, string> credentials, DeploymentProviderDefinition definition)
        {
            var results = new List<ActionResult> { };

            var uri = settings.Parameters.FirstOrDefault(c => c.Key == "vault_uri")?.Value;
            if (string.IsNullOrEmpty(uri))
            {
                results.Add(new ActionResult("Vault URI is required e.g. https://<vault-name>.vault.azure.net/", false));
            }

            var cert_name = settings.Parameters.FirstOrDefault(c => c.Key == "cert_name")?.Value;
            if (!string.IsNullOrEmpty(cert_name))
            {
                if (!Regex.IsMatch(cert_name, "^[0-9a-zA-Z-]+$"))
                {
                    results.Add(new ActionResult("Vault URI is required e.g. https://<vault-name>.vault.azure.net/", false));
                }
            }
            return results;
        }
    }
}
