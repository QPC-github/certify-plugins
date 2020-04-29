﻿# This is an example script and it will be overwritten when the next update is installed. 
# To use this script copy it to another location and modify as required

# This script enables the use of the newly retrieved and stored certificate with common Exchange services
# For more script info see https://docs.certifytheweb.com/docs/script-hooks.html

param($result, $services, [switch] $cleanupPreviousCerts = $false)

# enable powershell snap-in for Exchange 2010 upwards
Add-PSSnapIn Microsoft.Exchange.Management.PowerShell.E2010


Write-Host "Enabling Certificate for Exchange services.."
		
# tell Exchange which services to use this certificate for, force accept certificate to avoid command line prompt
Enable-ExchangeCertificate -Thumbprint $result.ManagedItem.CertificateThumbprintHash -Services $services -Force -ErrorAction Stop

Write-Host "Certificate set OK for services."


if ($cleanupPreviousCerts -eq $true)
{
	Write-Host "Cleaning up previous certs in Exchange"
	
	Get-ExchangeCertificate -DomainName $Certificate.Subject.split("=")[1] | Where-Object -FilterScript { $_.Thumbprint -ne $NewCertThumbprint} | Remove-ExchangeCertificate -Confirm:$false
}