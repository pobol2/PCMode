$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot
try {
    $files = @(
        "README.md", "SECURITY.md", "THIRD_PARTY_NOTICES.md",
        "PCMode_Product_Roadmap.md", "LICENSE",
        "app/ARCHITECTURE.md", "app/AUDIO_ARCHITECTURE.md",
        "app/BUILD_AND_TEST.md", "app/DEVELOPMENT_PRINCIPLES.md",
        "app/src/PCMode.Tray/ARCHITECTURE.md",
        "app/config/pcmode.example.json"
    )

    tar -a -c -f "PCMode_Documentation.zip" $files
    if ($LASTEXITCODE) { throw "Could not create the documentation archive." }
}
finally {
    Pop-Location
}
