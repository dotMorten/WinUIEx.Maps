param(
    [string]$SecretsProject = (
        Join-Path $PSScriptRoot '..\WinUIEx.Maps.Tests\WinUIEx.Maps.Tests.csproj')
)

$ErrorActionPreference = 'Stop'
$secretLine = dotnet user-secrets list --project $SecretsProject 2>$null |
    Where-Object { $_ -like 'AzureMaps:MapServiceToken = *' } |
    Select-Object -First 1
if (-not $secretLine) {
    throw 'Configure AzureMaps:MapServiceToken in the test project user secrets first.'
}

$token = $secretLine.Substring($secretLine.IndexOf(' = ') + 3)
$fixtures = @(
    @{ Name = 'new-york-z10'; Longitude = -74.0060; Latitude = 40.7128; Zoom = 10 },
    @{ Name = 'seattle-z12'; Longitude = -122.3321; Latitude = 47.6062; Zoom = 12 },
    @{ Name = 'new-york-z14'; Longitude = -74.0060; Latitude = 40.7128; Zoom = 14 },
    @{ Name = 'tokyo-z16'; Longitude = 139.6917; Latitude = 35.6895; Zoom = 16 }
)

function Get-TileCoordinate(
    [double]$Longitude,
    [double]$Latitude,
    [int]$Zoom
) {
    $count = [Math]::Pow(2, $Zoom)
    $x = [Math]::Floor(($Longitude + 180) / 360 * $count)
    $latitudeRadians = $Latitude * [Math]::PI / 180
    $y = [Math]::Floor(
        (1 - [Math]::Log(
            [Math]::Tan($latitudeRadians) +
            (1 / [Math]::Cos($latitudeRadians))) / [Math]::PI) / 2 * $count)
    return @([int]$x, [int]$y)
}

try {
    foreach ($fixture in $fixtures) {
        $tile = Get-TileCoordinate `
            $fixture.Longitude `
            $fixture.Latitude `
            $fixture.Zoom
        $uri =
            'https://atlas.microsoft.com/map/tile' +
            '?api-version=2024-04-01' +
            '&tilesetId=microsoft.base' +
            "&zoom=$($fixture.Zoom)&x=$($tile[0])&y=$($tile[1])"
        $path = Join-Path $PSScriptRoot "Fixtures\$($fixture.Name).pbf"
        Invoke-WebRequest `
            -Uri $uri `
            -Headers @{
                'subscription-key' = $token
                Accept = 'application/vnd.mapbox-vector-tile'
            } `
            -OutFile $path
        Get-Item $path |
            Select-Object Name, Length
    }
}
finally {
    Remove-Variable token, secretLine -ErrorAction SilentlyContinue
}
