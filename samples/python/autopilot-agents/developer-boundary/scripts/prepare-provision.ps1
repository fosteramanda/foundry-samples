$ErrorActionPreference = "Stop"

function Get-AzdValue {
    param([Parameter(Mandatory)][string]$Name)

    $value = azd env get-value $Name 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) {
        return ""
    }

    return $value.Trim()
}

function Set-AzdValue {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value
    )

    azd env set $Name $Value | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to set azd environment value $Name."
    }
}

function Read-RequiredValue {
    param([Parameter(Mandatory)][string]$Prompt)

    if ([Console]::IsInputRedirected) {
        throw "Cannot prompt for '$Prompt' in non-interactive mode."
    }

    do {
        $value = (Read-Host $Prompt).Trim()
    } while ([string]::IsNullOrWhiteSpace($value))

    return $value
}

function Get-RequiredAzdValue {
    param(
        [Parameter(Mandatory)][string]$EnvironmentName,
        [Parameter(Mandatory)][string]$Prompt
    )

    $value = Get-AzdValue -Name $EnvironmentName
    if (-not [string]::IsNullOrWhiteSpace($value)) {
        return $value
    }

    if ([Console]::IsInputRedirected) {
        throw "Missing $EnvironmentName. Set it with 'azd env set $EnvironmentName <value>' before running non-interactively."
    }

    $value = Read-RequiredValue -Prompt $Prompt
    Set-AzdValue -Name $EnvironmentName -Value $value
    return $value
}

function Read-MenuChoice {
    param(
        [Parameter(Mandatory)][string]$Prompt,
        [Parameter(Mandatory)][int]$Maximum
    )

    do {
        $choice = Read-Host "$Prompt (1-$Maximum)"
    } while ($choice -notmatch '^\d+$' -or [int]$choice -lt 1 -or [int]$choice -gt $Maximum)

    return [int]$choice
}

function Get-ExistingNames {
    param([Parameter(Mandatory)][scriptblock]$Query)

    $global:LASTEXITCODE = 0
    $items = @(& $Query)
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query existing Azure resources."
    }

    return @(
        $items |
            ForEach-Object { ([string]$_).Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
}

function Select-ExistingName {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string[]]$Names
    )

    Write-Host ""
    Write-Host "Select an existing ${Label}:"
    for ($index = 0; $index -lt $Names.Count; $index++) {
        Write-Host "  $($index + 1)) $($Names[$index])"
    }

    $choice = Read-MenuChoice -Prompt "Selection" -Maximum $Names.Count
    return $Names[$choice - 1]
}

function Select-Resource {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$ModeEnvironmentName,
        [Parameter(Mandatory)][string]$NameEnvironmentName,
        [Parameter(Mandatory)][string]$NewNamePrompt,
        [Parameter(Mandatory)][scriptblock]$ExistingNamesQuery
    )

    $mode = Get-AzdValue -Name $ModeEnvironmentName
    $modeWasConfigured = $mode -in @("reuse", "new")

    if (-not $modeWasConfigured) {
        if ([Console]::IsInputRedirected) {
            throw "Missing $ModeEnvironmentName. Set it to 'reuse' or 'new' before running non-interactively."
        }

        Write-Host ""
        Write-Host "${Label}:"
        Write-Host "  1) Reuse an existing $Label"
        Write-Host "  2) Create a new $Label"
        $mode = if ((Read-MenuChoice -Prompt "Selection" -Maximum 2) -eq 1) {
            "reuse"
        } else {
            "new"
        }
        Set-AzdValue -Name $ModeEnvironmentName -Value $mode
    }

    $existingNames = Get-ExistingNames -Query $ExistingNamesQuery
    $name = Get-AzdValue -Name $NameEnvironmentName

    if ($mode -eq "reuse") {
        if ($existingNames.Count -eq 0) {
            Write-Host "No existing $Label was found in the selected scope. A new one is required."
            $mode = "new"
            Set-AzdValue -Name $ModeEnvironmentName -Value $mode
        } elseif ([string]::IsNullOrWhiteSpace($name) -or $name -notin $existingNames) {
            if ([Console]::IsInputRedirected) {
                throw "Set $NameEnvironmentName to one of the existing $Label names before running non-interactively."
            }
            $name = Select-ExistingName -Label $Label -Names $existingNames
            Set-AzdValue -Name $NameEnvironmentName -Value $name
        }
    }

    if ($mode -eq "new") {
        if (-not $modeWasConfigured) {
            $name = ""
        }

        if ([string]::IsNullOrWhiteSpace($name)) {
            $name = Read-RequiredValue -Prompt $NewNamePrompt
            Set-AzdValue -Name $NameEnvironmentName -Value $name
        }

        if ($name -in $existingNames) {
            if (-not $modeWasConfigured) {
                throw "$Label '$name' already exists in the selected scope. Run azd provision again and choose reuse."
            }

            Write-Host "$Label '$name' now exists; reusing it."
            $mode = "reuse"
            Set-AzdValue -Name $ModeEnvironmentName -Value $mode
        }
    }

    return [pscustomobject]@{
        Name = $name
        Exists = $mode -eq "reuse"
    }
}

function Set-BooleanAzdValue {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Value
    )

    Set-AzdValue -Name $Name -Value $Value.ToString().ToLowerInvariant()
}

function Get-AzureSubscriptionId {
    $subscriptionId = Get-AzdValue -Name "AZURE_SUBSCRIPTION_ID"
    if (-not [string]::IsNullOrWhiteSpace($subscriptionId)) {
        return $subscriptionId
    }

    if ([Console]::IsInputRedirected) {
        throw "Missing AZURE_SUBSCRIPTION_ID. Set it with 'azd env set AZURE_SUBSCRIPTION_ID <subscription-id>' before running non-interactively."
    }

    $subscriptionsJson = az account list `
        --query "[?state=='Enabled'].{Name:name,Id:id,IsDefault:isDefault}" `
        --output json `
        --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to list Azure subscriptions. Run 'az login' and try again."
    }

    $subscriptions = @(
        $subscriptionsJson |
            ConvertFrom-Json |
            Sort-Object @{ Expression = { -not $_.IsDefault } }, Name
    )
    if ($subscriptions.Count -eq 0) {
        throw "No enabled Azure subscriptions were found. Run 'az login' and try again."
    }

    $defaultSubscription = $subscriptions |
        Where-Object { $_.IsDefault } |
        Select-Object -First 1
    do {
        Write-Host ""
        if ($null -ne $defaultSubscription) {
            Write-Host "Default subscription: $($defaultSubscription.Name) [$($defaultSubscription.Id)]"
            $search = Read-Host "Subscription name or ID (press Enter to use the default)"
        } else {
            $search = Read-Host "Subscription name or ID"
        }

        if ([string]::IsNullOrWhiteSpace($search) -and $null -ne $defaultSubscription) {
            $selectedSubscription = $defaultSubscription
            continue
        }

        $search = $search.Trim()
        $matches = @(
            $subscriptions |
                Where-Object {
                    $_.Id -ieq $search -or
                    $_.Name -ieq $search -or
                    $_.Name.IndexOf($search, [StringComparison]::OrdinalIgnoreCase) -ge 0
                }
        )

        if ($matches.Count -eq 0) {
            Write-Host "No enabled subscription matched '$search'."
        } elseif ($matches.Count -eq 1) {
            $selectedSubscription = $matches[0]
        } elseif ($matches.Count -gt 20) {
            Write-Host "$($matches.Count) subscriptions matched. Enter a more specific name or ID."
        } else {
            Write-Host ""
            Write-Host "Select an Azure subscription:"
            for ($index = 0; $index -lt $matches.Count; $index++) {
                Write-Host "  $($index + 1)) $($matches[$index].Name) [$($matches[$index].Id)]"
            }
            $choice = Read-MenuChoice -Prompt "Selection" -Maximum $matches.Count
            $selectedSubscription = $matches[$choice - 1]
        }
    } while ($null -eq $selectedSubscription)

    $subscriptionId = $selectedSubscription.Id
    Set-AzdValue -Name "AZURE_SUBSCRIPTION_ID" -Value $subscriptionId
    return $subscriptionId
}

function Get-AzureLocation {
    param([Parameter(Mandatory)][string]$SubscriptionId)

    $location = Get-AzdValue -Name "AZURE_LOCATION"
    if (-not [string]::IsNullOrWhiteSpace($location)) {
        return $location
    }

    if ([Console]::IsInputRedirected) {
        throw "Missing AZURE_LOCATION. Set it with 'azd env set AZURE_LOCATION <location>' before running non-interactively."
    }

    $availableLocations = Get-ExistingNames -Query {
        az rest `
            --method get `
            --url "https://management.azure.com/subscriptions/$SubscriptionId/locations?api-version=2022-12-01" `
            --query "value[].name" `
            --output tsv `
            --only-show-errors
    }

    do {
        $location = Read-RequiredValue -Prompt "Azure location (for example, eastus2)"
        $matchedLocation = $availableLocations |
            Where-Object { $_ -ieq $location } |
            Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($matchedLocation)) {
            Write-Host "Location '$location' is not available in the selected subscription."
        }
    } while ([string]::IsNullOrWhiteSpace($matchedLocation))

    Set-AzdValue -Name "AZURE_LOCATION" -Value $matchedLocation
    return $matchedLocation
}

function Get-DefaultModelVersion {
    param(
        [Parameter(Mandatory)][string]$SubscriptionId,
        [Parameter(Mandatory)][string]$Location,
        [Parameter(Mandatory)][string]$ModelName
    )

    $modelsUrl = "https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.CognitiveServices/locations/$Location/models?api-version=2025-06-01"
    $modelsJson = az rest `
        --method get `
        --url $modelsUrl `
        --output json `
        --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query models available in '$Location'."
    }

    $matchingModels = @(
        ($modelsJson | ConvertFrom-Json).value |
            Where-Object {
                $_.model.format -eq "OpenAI" -and
                $_.model.name -ieq $ModelName -and
                $_.model.isDefaultVersion -eq $true -and
                "GlobalStandard" -in @($_.model.skus.name)
            }
    )
    $versions = @(
        $matchingModels.model.version |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )

    if ($versions.Count -eq 0) {
        throw "No default version of OpenAI model '$ModelName' with the GlobalStandard SKU is available in '$Location'. Check the model name and region."
    }
    if ($versions.Count -gt 1) {
        throw "Azure returned multiple default versions for OpenAI model '$ModelName' in '$Location': $($versions -join ', ')."
    }

    return $versions[0]
}

$subscriptionId = Get-AzureSubscriptionId
$location = Get-AzureLocation -SubscriptionId $subscriptionId

$resourceGroup = Select-Resource `
    -Label "resource group" `
    -ModeEnvironmentName "RESOURCE_GROUP_MODE" `
    -NameEnvironmentName "AZURE_RESOURCE_GROUP" `
    -NewNamePrompt "New resource group name" `
    -ExistingNamesQuery {
        az group list `
            --subscription $subscriptionId `
            --query "[].name" `
            --output tsv
    }

$foundryResource = Select-Resource `
    -Label "Foundry resource" `
    -ModeEnvironmentName "FOUNDRY_RESOURCE_MODE" `
    -NameEnvironmentName "AZURE_AI_ACCOUNT_NAME" `
    -NewNamePrompt "New Foundry resource name" `
    -ExistingNamesQuery {
        if (-not $resourceGroup.Exists) {
            return
        }
        az cognitiveservices account list `
            --resource-group $resourceGroup.Name `
            --subscription $subscriptionId `
            --query "[?kind=='AIServices'].name" `
            --output tsv
    }

$configuredProjectName = Get-AzdValue -Name "AZURE_AI_PROJECT_NAME"
if (
    (Get-AzdValue -Name "FOUNDRY_PROJECT_MODE") -eq "reuse" -and
    $configuredProjectName.Contains("/")
) {
    Set-AzdValue `
        -Name "AZURE_AI_PROJECT_NAME" `
        -Value (($configuredProjectName -split "/")[-1])
}

$foundryProject = Select-Resource `
    -Label "Foundry project" `
    -ModeEnvironmentName "FOUNDRY_PROJECT_MODE" `
    -NameEnvironmentName "AZURE_AI_PROJECT_NAME" `
    -NewNamePrompt "New Foundry project name" `
    -ExistingNamesQuery {
        if (-not $foundryResource.Exists) {
            return
        }
        $projectNames = az cognitiveservices account project list `
            --name $foundryResource.Name `
            --resource-group $resourceGroup.Name `
            --subscription $subscriptionId `
            --query "[].name" `
            --output tsv
        if ($LASTEXITCODE -eq 0) {
            $projectNames | ForEach-Object { ($_ -split "/")[-1] }
        }
    }

$modelDeployment = Select-Resource `
    -Label "model deployment" `
    -ModeEnvironmentName "MODEL_DEPLOYMENT_MODE" `
    -NameEnvironmentName "AZURE_AI_MODEL_DEPLOYMENT_NAME" `
    -NewNamePrompt "New model deployment name" `
    -ExistingNamesQuery {
        if (-not $foundryResource.Exists) {
            return
        }
        az cognitiveservices account deployment list `
            --name $foundryResource.Name `
            --resource-group $resourceGroup.Name `
            --subscription $subscriptionId `
            --query "[].name" `
            --output tsv
    }

$developerPrincipalId = Get-AzdValue -Name "DEVELOPER_PRINCIPAL_ID"
if ([string]::IsNullOrWhiteSpace($developerPrincipalId)) {
    $developerPrincipalId = az ad signed-in-user show --query id --output tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($developerPrincipalId)) {
        $developerPrincipalId = Get-RequiredAzdValue `
            -EnvironmentName "DEVELOPER_PRINCIPAL_ID" `
            -Prompt "Microsoft Entra object ID of the developer"
    } else {
        $developerPrincipalId = $developerPrincipalId.Trim()
        Set-AzdValue -Name "DEVELOPER_PRINCIPAL_ID" -Value $developerPrincipalId
    }
}

Set-BooleanAzdValue -Name "RESOURCE_GROUP_EXISTS" -Value $resourceGroup.Exists
Set-BooleanAzdValue -Name "FOUNDRY_RESOURCE_EXISTS" -Value $foundryResource.Exists
Set-BooleanAzdValue -Name "FOUNDRY_PROJECT_EXISTS" -Value $foundryProject.Exists
Set-BooleanAzdValue -Name "MODEL_DEPLOYMENT_EXISTS" -Value $modelDeployment.Exists

if ($modelDeployment.Exists) {
    $modelDeploymentDetails = az cognitiveservices account deployment show `
        --name $foundryResource.Name `
        --resource-group $resourceGroup.Name `
        --deployment-name $modelDeployment.Name `
        --subscription $subscriptionId `
        --output json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $null -eq $modelDeploymentDetails) {
        throw "Failed to read model deployment '$($modelDeployment.Name)'."
    }

    Set-AzdValue -Name "MODEL_NAME" -Value $modelDeploymentDetails.properties.model.name
    Set-AzdValue -Name "MODEL_FORMAT" -Value $modelDeploymentDetails.properties.model.format
    Set-AzdValue -Name "MODEL_VERSION" -Value $modelDeploymentDetails.properties.model.version
    Set-AzdValue -Name "MODEL_SKU_NAME" -Value $modelDeploymentDetails.sku.name
    Set-AzdValue -Name "MODEL_CAPACITY" -Value ([string]$modelDeploymentDetails.sku.capacity)
} else {
    Set-AzdValue -Name "MODEL_FORMAT" -Value "OpenAI"
    Set-AzdValue -Name "MODEL_SKU_NAME" -Value "GlobalStandard"
    Set-AzdValue -Name "MODEL_CAPACITY" -Value "1"
    $modelName = Get-RequiredAzdValue -EnvironmentName "MODEL_NAME" -Prompt "OpenAI model name"
    $modelVersion = Get-DefaultModelVersion `
        -SubscriptionId $subscriptionId `
        -Location $location `
        -ModelName $modelName
    Set-AzdValue -Name "MODEL_VERSION" -Value $modelVersion
    Write-Host "Using default model version '$modelVersion'."
}

Write-Host ""
Write-Host "Provisioning plan:"
Write-Host "  Resource group '$($resourceGroup.Name)': $(if ($resourceGroup.Exists) { 'reuse' } else { 'create' })"
Write-Host "  Foundry resource '$($foundryResource.Name)': $(if ($foundryResource.Exists) { 'reuse' } else { 'create' })"
Write-Host "  Foundry project '$($foundryProject.Name)': $(if ($foundryProject.Exists) { 'reuse' } else { 'create' })"
Write-Host "  Model deployment '$($modelDeployment.Name)': $(if ($modelDeployment.Exists) { 'reuse' } else { 'create' })"
