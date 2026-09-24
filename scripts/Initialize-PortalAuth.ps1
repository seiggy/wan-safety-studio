#requires -Version 7.4
[CmdletBinding()]
param(
    [switch]$ApproveIdentityChanges,
    [switch]$ApproveAdminConsent,
    [switch]$RotateSecret,
    [string]$ConfigPath = (Join-Path (Split-Path $PSScriptRoot) 'infra' 'terraform.tfvars.json'),
    [string]$CacheDirectory = $env:WAN_STUDIO_CACHE
)

$ErrorActionPreference = 'Stop'
$selectedConfig = $ConfigPath
$selectedCache = $CacheDirectory
. (Join-Path $PSScriptRoot 'Invoke-WanSafetyStudio.ps1') -Action Status -ConfigPath $selectedConfig -CacheDirectory $selectedCache
Assert-True $ApproveIdentityChanges.IsPresent 'Requires -ApproveIdentityChanges: creates Entra app/group/assignment, adds the operator as a group member, grants vault-scoped Secrets Officer and, with an App Service portal, adds its redirect URI and a managed-identity federated credential.'
Initialize-Configuration
Read-Foundation
Initialize-Demo
Assert-Foundation
$signedIn = (Invoke-Native az @('ad','signed-in-user','show','--query','id','--output','tsv','--only-show-errors') -join '').Trim()
Assert-True ($signedIn -ieq $Config.operator_principal_id) 'Sign in as the configured operator user before initializing local portal authentication.'
$raw = Invoke-Native az @('account','get-access-token','--resource','https://graph.microsoft.com/','--query','accessToken','--output','tsv','--only-show-errors')
$graphToken = ConvertTo-SecureString ($raw -join '').Trim() -AsPlainText -Force
$raw = $null

function Invoke-Graph([string]$Path, [string]$Method = 'GET', $Body = $null) {
    $uri = if ($Path.StartsWith('/')) { "https://graph.microsoft.com/v1.0$Path" } else { $Path }
    Assert-True ($uri.StartsWith('https://graph.microsoft.com/v1.0/')) 'Unexpected Graph endpoint.'
    $parameters = @{Uri=$uri; Method=$Method; Authentication='Bearer'; Token=$graphToken; TimeoutSec=60}
    if ($null -ne $Body) { $parameters.Body = ConvertTo-Json $Body -Depth 12 -Compress; $parameters.ContentType='application/json' }
    Invoke-RestMethod @parameters
}
function Get-GraphValues([string]$Path) {
    $page = Invoke-Graph $Path
    while ($page) {
        foreach ($item in $page.value) { $item }
        $page = if ($page.'@odata.nextLink') { Invoke-Graph $page.'@odata.nextLink' } else { $null }
    }
}

$appName = 'WAN Safety Studio'
$groupName = 'WAN Safety Studio Creators'
$redirect = 'http://localhost:51881/auth/callback'
$secretName = 'wan-studio-msal'
$deploymentTag = "deployment:$($Config.deployment_name)"
$apps = @(Get-GraphValues ('/applications?$filter=' + [uri]::EscapeDataString("displayName eq '$appName'")))
Assert-True ($apps.Count -le 1) 'Multiple WAN Safety Studio registrations exist; resolve ambiguity with the identity owner.'
if ($apps.Count) {
    $app = $apps[0]
    Assert-True ($app.signInAudience -eq 'AzureADMyOrg' -and 'wan-safety-studio' -in $app.tags -and
        $deploymentTag -in $app.tags -and $redirect -in $app.web.redirectUris) 'Existing registration is not owned by this deployment or has incompatible sign-in settings.'
} else {
    $app = Invoke-Graph '/applications' 'POST' @{
        displayName=$appName; signInAudience='AzureADMyOrg'
        tags=@('wan-safety-studio',$deploymentTag)
        web=@{redirectUris=@($redirect)}
        appRoles=@(@{
            id=[guid]::NewGuid().ToString(); allowedMemberTypes=@('User')
            displayName='Video Creator'; description='Sign in to WAN Safety Studio and use operator-approved generation.'
            isEnabled=$true; value='VideoCreator'
        })
    }
}
$roles = @($app.appRoles | Where-Object { $_.value -ceq 'VideoCreator' -and $_.isEnabled })
Assert-True ($roles.Count -eq 1) 'Registration must expose exactly one enabled VideoCreator role.'
$roleId = $roles[0].id
$hostedRedirect = $null
$federatedSubject = $null
if ($Foundation.portal) {
    # App Service host: add its HTTPS callback and trust its managed identity instead of a secret.
    $hostedRedirect = "https://$($Foundation.portal.hostname)/auth/callback"
    if ($hostedRedirect -cnotin $app.web.redirectUris) {
        $null = Invoke-Graph "/applications/$($app.id)" 'PATCH' @{web=@{redirectUris=@(@($app.web.redirectUris) + $hostedRedirect)}}
    }
    $federatedSubject = $Foundation.portal.identityPrincipalId
    $credential = @{
        name="wan-portal-$($Config.deployment_name)"; issuer="https://login.microsoftonline.com/$Tenant/v2.0"
        subject=$federatedSubject; audiences=@('api://AzureADTokenExchange')
        description='WAN Safety Studio App Service managed identity (secretless MSAL client assertion).'
    }
    $existingCredential = @(Get-GraphValues "/applications/$($app.id)/federatedIdentityCredentials" | Where-Object name -ceq $credential.name)
    if (-not $existingCredential.Count) {
        $null = Invoke-Graph "/applications/$($app.id)/federatedIdentityCredentials" 'POST' $credential
    } elseif ($existingCredential[0].subject -ne $federatedSubject -or $existingCredential[0].issuer -ne $credential.issuer) {
        $null = Invoke-Graph "/applications/$($app.id)/federatedIdentityCredentials/$($existingCredential[0].id)" 'PATCH' @{
            issuer=$credential.issuer; subject=$federatedSubject; audiences=$credential.audiences
        }
    }
}
$principals = @(Get-GraphValues ('/servicePrincipals?$filter=' + [uri]::EscapeDataString("appId eq '$($app.appId)'")))
Assert-True ($principals.Count -le 1) 'Ambiguous portal service principal.'
$principal = if ($principals.Count) { $principals[0] } else { Invoke-Graph '/servicePrincipals' 'POST' @{appId=$app.appId; appRoleAssignmentRequired=$true} }
if (-not $principal.appRoleAssignmentRequired) {
    $null = Invoke-Graph "/servicePrincipals/$($principal.id)" 'PATCH' @{appRoleAssignmentRequired=$true}
}

$groups = @(Get-GraphValues ('/groups?$filter=' + [uri]::EscapeDataString("displayName eq '$groupName'")))
Assert-True ($groups.Count -le 1) 'Multiple creator groups exist; resolve ambiguity with the identity owner.'
$nickname = "$($Config.deployment_name)-wan-creators"
if ($groups.Count) {
    $group = $groups[0]
    Assert-True ($group.securityEnabled -and $group.mailNickname -eq $nickname) 'Existing creator group does not match this deployment.'
} else {
    $group = Invoke-Graph '/groups' 'POST' @{
        displayName=$groupName; mailEnabled=$false; securityEnabled=$true; mailNickname=$nickname
        description="WAN Safety Studio creators for $($Config.deployment_name). Managed by Initialize-PortalAuth.ps1."
    }
}
$members = @(Get-GraphValues "/groups/$($group.id)/members?`$select=id")
if ($signedIn -notin $members.id) {
    $null = Invoke-Graph "/groups/$($group.id)/members/`$ref" 'POST' @{
        '@odata.id'="https://graph.microsoft.com/v1.0/directoryObjects/$signedIn"
    }
}
$assignments = @(Get-GraphValues "/servicePrincipals/$($principal.id)/appRoleAssignedTo")
if (-not @($assignments | Where-Object { $_.principalId -eq $group.id -and $_.appRoleId -eq $roleId }).Count) {
    $null = Invoke-Graph "/servicePrincipals/$($principal.id)/appRoleAssignedTo" 'POST' @{
        principalId=$group.id; resourceId=$principal.id; appRoleId=$roleId
    }
}

if ($ApproveAdminConsent) {
    $graphAppId = '00000003-0000-0000-c000-000000000000'
    $graphPrincipals = @(Get-GraphValues ('/servicePrincipals?$filter=' + [uri]::EscapeDataString("appId eq '$graphAppId'")))
    Assert-True ($graphPrincipals.Count -eq 1) 'Cannot resolve the Microsoft Graph service principal for sign-in scopes.'
    $graphPrincipal = $graphPrincipals[0]
    $scopes = @($graphPrincipal.oauth2PermissionScopes | Where-Object { $_.value -cin @('openid','profile') -and $_.isEnabled })
    Assert-True ($scopes.Count -eq 2) 'The required OpenID sign-in scope definitions are unavailable.'
    foreach ($resource in $app.requiredResourceAccess) {
        Assert-True ($resource.resourceAppId -eq $graphAppId -and
            @($resource.resourceAccess | Where-Object { $_.type -ne 'Scope' -or $_.id -notin $scopes.id }).Count -eq 0) 'Existing app declares additional permissions; review them with the identity owner before granting consent.'
    }
    $null = Invoke-Graph "/applications/$($app.id)" 'PATCH' @{
        requiredResourceAccess=@(@{
            resourceAppId=$graphAppId
            resourceAccess=@($scopes | ForEach-Object { @{id=$_.id; type='Scope'} })
        })
    }
    $filter = [uri]::EscapeDataString("clientId eq '$($principal.id)'")
    $grants = @(Get-GraphValues "/oauth2PermissionGrants?`$filter=$filter" |
        Where-Object { $_.consentType -eq 'AllPrincipals' -and $_.resourceId -eq $graphPrincipal.id })
    Assert-True ($grants.Count -le 1) 'Ambiguous tenant-wide consent; have the identity owner review it.'
    if ($grants.Count) {
        Assert-True (@($grants[0].scope -split ' ' | Where-Object { $_ -and $_ -cnotin @('openid','profile') }).Count -eq 0) 'Existing consent includes additional scopes; refusing to change it automatically.'
        if (@($grants[0].scope -split ' ' | Where-Object { $_ -cin @('openid','profile') }).Count -ne 2) {
            $null = Invoke-Graph "/oauth2PermissionGrants/$($grants[0].id)" 'PATCH' @{scope='openid profile'}
        }
    } else {
        $null = Invoke-Graph '/oauth2PermissionGrants' 'POST' @{
            clientId=$principal.id; consentType='AllPrincipals'; resourceId=$graphPrincipal.id; scope='openid profile'
        }
    }
    Write-Host 'Admin consent granted only for openid and profile. Creator-group assignment remains required.'
}

$officer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
$grants = Invoke-Native az @('role','assignment','list','--assignee',$signedIn,'--scope',$Foundation.keyVaultId,
    '--include-inherited','--output','json','--only-show-errors') | ConvertFrom-Json
if (-not @($grants | Where-Object { $_.roleDefinitionName -in @('Key Vault Secrets Officer','Key Vault Administrator') }).Count) {
    $null = Invoke-Native az @('role','assignment','create','--assignee-object-id',$signedIn,'--assignee-principal-type','User',
        '--role',$officer,'--scope',$Foundation.keyVaultId,'--output','none','--only-show-errors')
}
$raw = Invoke-Native az @('account','get-access-token','--resource','https://vault.azure.net','--query','accessToken','--output','tsv','--only-show-errors')
$vaultToken = ConvertTo-SecureString ($raw -join '').Trim() -AsPlainText -Force
$raw = $null
$secretUri = "https://$($Foundation.keyVaultName).vault.azure.net/secrets/${secretName}?api-version=7.4"
$deadline = [DateTime]::UtcNow.AddMinutes(5)
do {
    $existing = Invoke-RestMethod -Uri $secretUri -Authentication Bearer -Token $vaultToken -TimeoutSec 60 `
        -SkipHttpErrorCheck -StatusCodeVariable code
    if ($code -ne 403) { break }
    Assert-True ([DateTime]::UtcNow -lt $deadline) 'Vault data access is still denied. Check scoped RBAC, private DNS and VPN; rerun this script after correction.'
    Write-Host 'Waiting for vault-scoped RBAC propagation...'
    Start-Sleep -Seconds 10
} while ($true)
Assert-True ($code -in @(200,404)) "Vault secret lookup failed (HTTP $code); no password was created."
if ($code -eq 200) {
    Assert-True ($existing.tags.clientId -eq $app.appId -and $existing.tags.deployment -eq $Config.deployment_name) 'Existing vault secret belongs to a different registration/deployment.'
    Assert-True ($RotateSecret -or ($existing.attributes.enabled -and $existing.attributes.exp -gt [DateTimeOffset]::UtcNow.ToUnixTimeSeconds())) 'Portal secret is disabled/expired; rerun with -RotateSecret after reviewing its use.'
}
if ($code -eq 404 -or $RotateSecret) {
    $expiry = [DateTimeOffset]::UtcNow.AddDays(90)
    $password = Invoke-Graph "/applications/$($app.id)/addPassword" 'POST' @{
        passwordCredential=@{displayName='WAN portal Key Vault credential'; endDateTime=$expiry.ToString('o')}
    }
    try {
        $body = @{
            value=$password.secretText
            attributes=@{enabled=$true; exp=$expiry.ToUnixTimeSeconds()}
            tags=@{clientId=$app.appId; deployment=$Config.deployment_name; credentialKeyId=$password.keyId}
        } | ConvertTo-Json -Depth 6 -Compress
        $null = Invoke-RestMethod -Uri $secretUri -Method Put -Authentication Bearer -Token $vaultToken `
            -ContentType 'application/json' -Body $body -TimeoutSec 60
    } catch {
        $null = Invoke-Graph "/applications/$($app.id)/removePassword" 'POST' @{keyId=$password.keyId}
        throw 'Could not persist the new credential in Key Vault; that new credential was revoked. Correct vault access and rerun.'
    } finally { $body=$null; $password=$null }
    if ($existing.tags.credentialKeyId) {
        $null = Invoke-Graph "/applications/$($app.id)/removePassword" 'POST' @{keyId=$existing.tags.credentialKeyId}
    }
    $expires = $expiry.ToString('o')
} else {
    $expires = [DateTimeOffset]::FromUnixTimeSeconds($existing.attributes.exp).ToString('o')
}
$existing=$null
$receipt = @{
    tenantId=$Tenant; clientId=$app.appId; applicationObjectId=$app.id; servicePrincipalId=$principal.id
    groupId=$group.id; role='VideoCreator'; secretName=$secretName; secretExpiresUtc=$expires; redirectUri=$redirect
    hostedRedirectUri=$hostedRedirect; federatedSubject=$federatedSubject
}
$pending = Join-Path $Cache 'portal-auth.pending'
$receipt | ConvertTo-Json | Set-Content -Encoding utf8 $pending
Move-Item -LiteralPath $pending -Destination (Join-Path $Cache 'portal-auth.json') -Force
Write-Host "Configured single-tenant MSAL login; creator group: $($group.id). Secret stored only in Key Vault; expires $expires."
if ($hostedRedirect) { Write-Host "App Service sign-in: $hostedRedirect (federated managed identity; no secret on the host)." }
Write-Host "Non-secret receipt: $(Join-Path $Cache 'portal-auth.json')"
