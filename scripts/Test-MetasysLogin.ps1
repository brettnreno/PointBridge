[CmdletBinding()]
param([string]$ServerOrigin)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
if (!$ServerOrigin) { $ServerOrigin = Read-Host 'Metasys HTTPS address (use the same address entered in Point Bridge)' }
$serverUri = $null
if (![Uri]::TryCreate($ServerOrigin, [UriKind]::Absolute, [ref]$serverUri) -or
    $serverUri.Scheme -ne 'https' -or $serverUri.UserInfo -or $serverUri.AbsolutePath -ne '/' -or $serverUri.Query -or $serverUri.Fragment) {
    throw 'Enter only the Metasys HTTPS origin, such as https://server.example.com. Do not include /UI or credentials.'
}
$origin = $serverUri.GetLeftPart([UriPartial]::Authority)
$credential = Get-Credential -Message 'Enter your local Metasys login. The script does not print or save it.'
if (!$credential) { throw 'No credentials entered.' }
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$handler.UseDefaultCredentials = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(20)
$summary = [ordered]@{ Code = 'not-started'; LoginHttpStatus = $null; IdentityHttpStatus = $null; LogoutHttpStatus = $null }
$token = $null
$stage = 'login'

function Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties | Where-Object { $_.Name -ceq $Name } | Select-Object -First 1
    if ($null -ne $property) { return $property.Value }
    return $null
}

function Send-Json([string]$Method, [string]$Path, [string]$Body, [string]$Bearer) {
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), ($origin + $Path))
    $response = $null
    try {
        if ($Body) { $request.Content = New-Object System.Net.Http.StringContent($Body, [Text.Encoding]::UTF8, 'application/json') }
        if ($Bearer) { $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $Bearer) }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $json = $null
        try { $json = ConvertFrom-Json -InputObject $content -ErrorAction Stop } catch { }
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Json = $json; Text = $content }
    }
    finally {
        if ($response) { $response.Dispose() }
        $request.Dispose()
    }
}

try {
    $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $password = $credential.GetNetworkCredential().Password
    $plain = [Text.Encoding]::UTF8.GetBytes(('^{0}|{1}|{2}$' -f $credential.UserName, $password, $timestamp))
    $pad = New-Object byte[] $plain.Length
    $mixed = New-Object byte[] $plain.Length
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $random.GetBytes($pad)
        for ($index = 0; $index -lt $plain.Length; $index++) { $mixed[$index] = $plain[$index] -bxor $pad[$index] }
        $payload = @{ username = $credential.UserName; password = $password; domain = ''; timestamp = $timestamp; challenge = [Convert]::ToBase64String($hash.ComputeHash($mixed)); verifier = [Convert]::ToBase64String($pad) }
        $body = ConvertTo-Json $payload -Compress
    }
    finally {
        [Array]::Clear($plain, 0, $plain.Length)
        [Array]::Clear($mixed, 0, $mixed.Length)
        [Array]::Clear($pad, 0, $pad.Length)
        $random.Dispose()
        $hash.Dispose()
    }
    $login = Send-Json 'POST' '/API/private-api/Authentication/LogIn' $body $null
    $password = $null
    $payload = $null
    $body = $null
    $summary.LoginHttpStatus = $login.Status
    $token = [string](Field $login.Json 'access_token')
    $summary.LoginJsonReceived = $null -ne $login.Json
    $summary.Authenticated = (Field $login.Json 'IsAuthenticated') -eq $true
    $summary.TermsFieldPresent = $null -ne (Field $login.Json 'IsTermsAndConditionsAccepted')
    $summary.TermsAccepted = (Field $login.Json 'IsTermsAndConditionsAccepted') -eq $true
    $summary.TokenPresent = ![string]::IsNullOrWhiteSpace($token)
    $summary.StorageIdPresent = ![string]::IsNullOrWhiteSpace([string](Field $login.Json 'UserStorageId'))
    if ($login.Status -ne 200) { $summary.Code = 'login-http-' + $login.Status }
    elseif (!$summary.LoginJsonReceived) { $summary.Code = 'login-json-invalid' }
    elseif (!$summary.Authenticated -or !$summary.TokenPresent) { $summary.Code = 'login-rejected' }
    elseif (!$summary.TermsAccepted) { $summary.Code = 'login-terms-not-confirmed' }
    else {
        $stage = 'identity'
        $identity = Send-Json 'GET' '/API/private-api/v6/userinfo' $null $token
        $summary.IdentityV6HttpStatus = $identity.Status
        $summary.IdentityRoute = '/API/private-api/v6/userinfo'
        if ($identity.Status -eq 404) {
            $identity = Send-Json 'GET' '/API/private-api/v5/userinfo' $null $token
            $summary.IdentityRoute = '/API/private-api/v5/userinfo'
            $summary.IdentityV5HttpStatus = $identity.Status
        }
        if ($identity.Status -eq 404) {
            $identity = Send-Json 'GET' '/API/private-api/User/GetUserInformation' $null $token
            $summary.IdentityRoute = '/API/private-api/User/GetUserInformation'
            $legacyText = if ($identity.Json -is [string]) { $identity.Json } else { $identity.Text }
            $legacyFields = @($legacyText -split ',')
            $summary.LegacyFieldCount = $legacyFields.Count
            $summary.LegacyUsernameMatches = $legacyFields.Count -eq 3 -and [string]::Equals($legacyFields[2].Trim(), $credential.UserName.Trim(), [StringComparison]::OrdinalIgnoreCase)
            $summary.LegacyAccessTypeNumeric = $legacyFields.Count -eq 3 -and $legacyFields[0].Trim() -match '^\d+$'
            $summary.LegacyAdministratorFlagBoolean = $legacyFields.Count -eq 3 -and $legacyFields[1].Trim() -match '^(?i:true|false)$'
        }
        $summary.IdentityHttpStatus = $identity.Status
        $summary.IdentityJsonReceived = $null -ne $identity.Json
        $profile = Field $identity.Json 'profile'
        $summary.ProfilePresent = $null -ne $profile
        $summary.CanonicalUsernamePresent = ![string]::IsNullOrWhiteSpace([string](Field $profile 'userName'))
        $summary.CanonicalUsernameMatches = [string]::Equals([string](Field $profile 'userName'), $credential.UserName.Trim(), [StringComparison]::OrdinalIgnoreCase)
        $summary.ProfileIdPresent = ![string]::IsNullOrWhiteSpace([string](Field $profile 'id'))
        $summary.AuthenticationTypePresent = ![string]::IsNullOrWhiteSpace([string](Field $profile 'authenticationType'))
        $summary.AuthenticationTypeIsMetasys = [string]::Equals([string](Field $profile 'authenticationType'), 'metasys', [StringComparison]::Ordinal)
        $status = Field $profile 'accountStatus'
        $summary.AccountStatusPresent = $null -ne $status
        $summary.AccountStatusShapeSupported = $null -eq $status -or $status -is [string] -or ($status -is [array] -and @($status | Where-Object { $_ -isnot [string] }).Count -eq 0)
        $statuses = @($status | ForEach-Object { ([string]$_).Trim() })
        $summary.AccountStatusKnown = @($statuses | Where-Object { $_ -and $_ -notin @('Active','Dormant','Disabled','Locked','Expired') }).Count -eq 0
        $summary.AccountIneligible = @($statuses | Where-Object { $_ -in @('Disabled','Locked','Expired') }).Count -gt 0
        if ($identity.Status -ne 200) { $summary.Code = 'identity-http-' + $identity.Status }
        elseif ($summary.IdentityRoute -eq '/API/private-api/User/GetUserInformation' -and $summary.LegacyUsernameMatches -and $summary.LegacyAccessTypeNumeric -and $summary.LegacyAdministratorFlagBoolean) { $summary.Code = 'legacy-identity-contract-observed' }
        elseif (!$summary.IdentityJsonReceived) { $summary.Code = 'identity-json-invalid' }
        elseif (!$summary.ProfilePresent -or !$summary.CanonicalUsernamePresent -or !$summary.ProfileIdPresent -or !$summary.AuthenticationTypePresent) { $summary.Code = 'identity-fields-missing' }
        elseif (!$summary.CanonicalUsernameMatches -or !$summary.AuthenticationTypeIsMetasys -or !$summary.StorageIdPresent -or !$summary.AccountStatusShapeSupported -or !$summary.AccountStatusKnown -or $summary.AccountIneligible) { $summary.Code = 'identity-rejected' }
        else { $summary.Code = 'verified-from-this-Windows-account' }
    }
}
catch {
    $summary.Code = $stage + '-transport-failed'
    for ($failure = $_.Exception; $null -ne $failure; $failure = $failure.InnerException) {
        if ($failure -is [Security.Authentication.AuthenticationException] -or
            ($failure -is [Net.WebException] -and $failure.Status -in @([Net.WebExceptionStatus]::TrustFailure, [Net.WebExceptionStatus]::SecureChannelFailure))) {
            $summary.Code = 'tls-trust-or-handshake-failed'
            break
        }
    }
}
finally {
    if ($token) {
        try { $logout = Send-Json 'GET' '/API/private-api/Authentication/LogOut' $null $token; $summary.LogoutHttpStatus = $logout.Status } catch { }
    }
    $token = $null
    $password = $null
    $payload = $null
    $body = $null
    $credential = $null
    $client.Dispose()
    $handler.Dispose()
    $summary | ConvertTo-Json -Depth 3
}
