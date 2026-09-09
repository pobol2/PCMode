param(
    [string]$Root = "C:\Tools\PCMode",

    # Install/update the freshly built native driver.
    [switch]$Install,

    # Build and test PCMode.Audio.DriverClient afterward.
    [switch]$TestManaged,

    # Leave PCMode processes running.
    [switch]$DontStopProcesses,

    # Just disable/unload the currently installed driver and exit.
    # Skips build, install, and all other steps.
    [switch]$Stop
)

$ErrorActionPreference = "Stop"

$script:Failures = 0
$script:Warnings = 0

function Write-Step
{
    param([string]$Text)

    Write-Host ""
    Write-Host "============================================================"
    Write-Host $Text
    Write-Host "============================================================"
}

function Pass
{
    param([string]$Text)

    Write-Host "[PASS] $Text" -ForegroundColor Green
}

function Warn
{
    param([string]$Text)

    $script:Warnings++
    Write-Host "[WARN] $Text" -ForegroundColor Yellow
}

function Fail
{
    param([string]$Text)

    $script:Failures++
    Write-Host "[FAIL] $Text" -ForegroundColor Red
}

function Require
{
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition)
    {
        Fail $Message
        throw $Message
    }

    Pass $Message
}

function Get-PCModeDevice
{

    $matches = @()

    $cimDevices = @(
        Get-CimInstance `
            -ClassName Win32_PnPEntity `
            -ErrorAction SilentlyContinue |
            Where-Object {
                @($_.HardwareID) |
                    Where-Object {
                        $_ -ieq "ROOT\PCMODEAUDIO"
                    }
                }
    )

    foreach ($cimDevice in $cimDevices)
    {

        $pnpDevice =
        Get-PnpDevice `
            -InstanceId $cimDevice.PNPDeviceID `
            -ErrorAction SilentlyContinue

        if ($pnpDevice)
        {
            $matches += $pnpDevice
        } else
        {
            # Keep enough information for diagnostics even if
            # Get-PnpDevice cannot materialize this particular instance.
            $matches += [PSCustomObject]@{
                Status       = $cimDevice.Status
                Class        = $cimDevice.PNPClass
                FriendlyName = $cimDevice.Name
                InstanceId   = $cimDevice.PNPDeviceID
            }
        }
    }

    return @($matches)
}

function Find-MSBuild
{
    # The 32-bit MSBuild host cannot load this WDK's driver-validation tools
    # (InfVerif, ApiValidator): this WDK version only ships x64/arm64 builds
    # of them, so a 32-bit MSBuild process fails looking for an x86 one that
    # does not exist. The 64-bit host (Bin\amd64\MSBuild.exe) works, so it is
    # tried first regardless of what is on PATH or what vswhere would
    # otherwise return first.
    $vswhere = Join-Path `
        ${env:ProgramFiles(x86)} `
        "Microsoft Visual Studio\Installer\vswhere.exe"

    if (Test-Path $vswhere)
    {
        $amd64 = (
            & $vswhere `
                -latest `
                -products * `
                -requires Microsoft.Component.MSBuild `
                -find "MSBuild\**\Bin\amd64\MSBuild.exe" |
                Select-Object -First 1
        )

        if (-not [string]::IsNullOrWhiteSpace($amd64))
        {
            return $amd64
        }
    }

    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue

    if ($command)
    {
        return $command.Source
    }

    if (-not (Test-Path $vswhere))
    {
        return $null
    }

    return (
        & $vswhere `
            -latest `
            -products * `
            -requires Microsoft.Component.MSBuild `
            -find "MSBuild\**\Bin\MSBuild.exe" |
            Select-Object -First 1
    )
}

function Find-DevCon
{
    $command = Get-Command devcon.exe -ErrorAction SilentlyContinue

    if ($command)
    {
        return $command.Source
    }

    $toolsRoot = Join-Path `
        ${env:ProgramFiles(x86)} `
        "Windows Kits\10\Tools"

    if (-not (Test-Path $toolsRoot))
    {
        return $null
    }

    $candidates = @(
        Get-ChildItem `
            -Path $toolsRoot `
            -Recurse `
            -File `
            -Filter "devcon.exe" `
            -ErrorAction SilentlyContinue |
            Where-Object {
                $_.FullName -match '\\x64\\devcon\.exe$'
            }
    )

    if ($candidates.Count -eq 0)
    {
        return $null
    }

    # Prefer the newest file rather than relying on lexicographic version ordering.
    return (
        $candidates |
            Sort-Object LastWriteTime -Descending |
            Select-Object -ExpandProperty FullName -First 1
    )
}

function Normalize-DriverPath
{
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path))
    {
        return $null
    }

    $normalized = $Path.Trim('"')

    if ($normalized.StartsWith("\SystemRoot", [StringComparison]::OrdinalIgnoreCase))
    {
        $normalized =
        $env:SystemRoot +
        $normalized.Substring("\SystemRoot".Length)
    }

    $normalized = $normalized -replace '^\\\?\?\\', ''

    return $normalized
}

try
{

    # -------------------------------------------------------------------------
    # 1. Environment
    # -------------------------------------------------------------------------

    Write-Step "1. Checking environment"

    $Root = [IO.Path]::GetFullPath($Root)

    Require `
    (Test-Path $Root) `
        "PCMode root exists: $Root"

    $identity =
    [Security.Principal.WindowsIdentity]::GetCurrent()

    $principal =
    [Security.Principal.WindowsPrincipal]$identity

    $isAdmin =
    $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )

    if ($Install -or $Stop)
    {
        Require `
            $isAdmin `
            "PowerShell is running as Administrator."
    } elseif ($isAdmin)
    {
        Pass "PowerShell is running as Administrator."
    } else
    {
        Warn "PowerShell is not running as Administrator. Build-only mode is still usable."
    }

    # -------------------------------------------------------------------------
    # 1b. Stop-only mode
    # -------------------------------------------------------------------------

    if ($Stop)
    {

        Write-Step "1b. Stopping PCMode audio driver"

        $devconExe = Find-DevCon

        Require `
        (-not [string]::IsNullOrWhiteSpace($devconExe)) `
            "DevCon x64 was found."

        Write-Host "DevCon:"
        Write-Host "    $devconExe"

        $devices = Get-PCModeDevice

        if ($devices.Count -eq 0)
        {
            Pass "No ROOT\PCMODEAUDIO device is installed. Nothing to stop."
        } else
        {
            if ($devices.Count -gt 1)
            {
                Warn "Multiple ROOT\PCMODEAUDIO devices exist. Disabling all of them."
            }

            # A live stream open on this endpoint -- most commonly
            # PCMode.Audio.Host's own client -- stops DevCon from disabling
            # it immediately: it can only flag it to disable on next reboot
            # instead. Killing PCMode's own processes first removes the one
            # blocker this script can actually do something about.
            $processNames = @(
                "PCMode.Audio.Host",
                "PCMode.Tray"
            )

            $running = @(
                Get-Process `
                    -Name $processNames `
                    -ErrorAction SilentlyContinue
            )

            if ($running.Count -eq 0)
            {
                Pass "No relevant PCMode processes are currently running."
            } elseif ($DontStopProcesses)
            {
                Warn "Relevant PCMode processes are still running; disabling live may fail."

                foreach ($p in $running)
                {
                    Write-Host "    $($p.ProcessName) PID=$($p.Id)"
                }
            } else
            {
                foreach ($p in $running)
                {
                    Write-Host "Stopping $($p.ProcessName) PID=$($p.Id)"
                    Stop-Process -Id $p.Id -Force
                }

                Pass "Relevant PCMode processes were stopped."

                # Give the driver a moment to see the client disconnect
                # before asking it to disable.
                Start-Sleep -Milliseconds 500
            }

            # Windows Audio Engine keeps a handle open on every *enabled*
            # audio endpoint the whole time it runs -- not just the default
            # device -- which is what actually blocks a live disable here,
            # regardless of any PCMode process. Stopping the audio services
            # around the disable releases that handle; both are restarted
            # afterward no matter what happens in between.
            $audioServicesStopped = $false

            try
            {
                Stop-Service -Name Audiosrv -Force -ErrorAction Stop
                Stop-Service -Name AudioEndpointBuilder -Force -ErrorAction Stop

                $audioServicesStopped = $true

                Pass "Windows Audio services were stopped."
            } catch
            {
                Warn "Could not stop Windows Audio services: $($_.Exception.Message)"
            }

            try
            {
                $stillRunning = @()
                $devconExitCode = 0

                for ($attempt = 1; $attempt -le 3; $attempt++)
                {

                    & $devconExe disable "ROOT\PCMODEAUDIO*"

                    $devconExitCode = $LASTEXITCODE

                    $devices = Get-PCModeDevice

                    $stillRunning = @(
                        $devices |
                            Where-Object {
                                $_.Status -eq "OK"
                            }
                    )

                    if ($stillRunning.Count -eq 0)
                    {
                        break
                    }

                    Write-Host "    Attempt $attempt/3: device still reports Status=OK. Retrying..."
                    Start-Sleep -Seconds 1
                }
            } finally
            {
                if ($audioServicesStopped)
                {
                    try
                    {
                        Start-Service -Name AudioEndpointBuilder -ErrorAction Stop
                        Start-Service -Name Audiosrv -ErrorAction Stop

                        Pass "Windows Audio services were restarted."
                    } catch
                    {
                        Fail "Windows Audio services could not be restarted: $($_.Exception.Message)"
                    }
                }
            }

            # DevCon's own exit code is not trusted here: it is non-zero both
            # for a genuine failure and for "flagged to disable on next
            # reboot", which are different outcomes. Whether the device's own
            # reported Status actually changed is what answers whether it is
            # stopped.
            if ($stillRunning.Count -eq 0)
            {
                Pass "PCMode audio driver is stopped."
            } else
            {
                Fail "PCMode audio driver could not be disabled live (DevCon exit code $devconExitCode)."

                Write-Host ""
                Write-Host "DevCon has flagged it to disable on next reboot, but something still" -ForegroundColor Yellow
                Write-Host "has it open. Reboot to let the already-queued disable complete." -ForegroundColor Yellow
            }
        }

        Write-Step "RESULT"

        Write-Host "Failures : $script:Failures"
        Write-Host "Warnings : $script:Warnings"

        if ($script:Failures -eq 0)
        {
            Write-Host "SUCCESS" -ForegroundColor Green
            exit 0
        }

        Write-Host "FAILED" -ForegroundColor Red
        exit 1
    }

    # -------------------------------------------------------------------------
    # 2. Find driver project
    # -------------------------------------------------------------------------

    Write-Step "2. Locating PCMode.Audio.VirtualEndpoint"

    $projects = @(
        Get-ChildItem `
            -Path $Root `
            -Recurse `
            -File `
            -Filter "PCMode.Audio.VirtualEndpoint.vcxproj" `
            -ErrorAction SilentlyContinue
    )

    Require `
    ($projects.Count -gt 0) `
        "PCMode.Audio.VirtualEndpoint.vcxproj was found."

    if ($projects.Count -gt 1)
    {
        Fail "Multiple PCMode.Audio.VirtualEndpoint projects were found."

        foreach ($p in $projects)
        {
            Write-Host "    $($p.FullName)"
        }

        throw "Multiple driver projects found. Remove obsolete copies."
    }

    $project = $projects[0]
    $driverDir = $project.Directory.FullName

    Write-Host "Driver project:"
    Write-Host "    $($project.FullName)"

    Require `
    ($project.FullName.StartsWith(
            $Root,
            [StringComparison]::OrdinalIgnoreCase
        )) `
        "Driver belongs to the current PCMode repository."

    if ($project.FullName -match "PCMode\.AudioLab")
    {
        Fail "Driver project belongs to old PCMode.AudioLab tree."
        throw "Old AudioLab driver project detected."
    }

    # -------------------------------------------------------------------------
    # 3. Validate source tree
    # -------------------------------------------------------------------------

    Write-Step "3. Checking native source tree"

    $cppFiles = @(
        Get-ChildItem `
            $driverDir `
            -Recurse `
            -File `
            -Filter "*.cpp"
    )

    $headerFiles = @(
        Get-ChildItem `
            $driverDir `
            -Recurse `
            -File `
            -Filter "*.h"
    )

    $inxFiles = @(
        Get-ChildItem `
            $driverDir `
            -Recurse `
            -File `
            -Filter "*.inx"
    )

    Require `
    ($cppFiles.Count -gt 0) `
        "Native .cpp source files exist."

    Require `
    ($headerFiles.Count -gt 0) `
        "Native .h source files exist."

    Require `
    ($inxFiles.Count -gt 0) `
        "Driver .inx file exists."

    Write-Host "CPP files : $($cppFiles.Count)"
    Write-Host "Headers   : $($headerFiles.Count)"
    Write-Host "INX files : $($inxFiles.Count)"

    # -------------------------------------------------------------------------
    # 4. Find MSBuild
    # -------------------------------------------------------------------------

    Write-Step "4. Finding Visual Studio / MSBuild"

    $msbuildExe = Find-MSBuild

    Require `
    (-not [string]::IsNullOrWhiteSpace($msbuildExe)) `
        "MSBuild was located."

    Write-Host "MSBuild:"
    Write-Host "    $msbuildExe"

    # -------------------------------------------------------------------------
    # 5. Stop PCMode processes
    # -------------------------------------------------------------------------

    Write-Step "5. Checking running PCMode audio processes"

    $processNames = @(
        "PCMode.Audio.Host",
        "PCMode.Tray"
    )

    $running = @(
        Get-Process `
            -Name $processNames `
            -ErrorAction SilentlyContinue
    )

    if ($running.Count -eq 0)
    {
        Pass "No relevant PCMode processes are currently running."
    } elseif ($DontStopProcesses)
    {
        Warn "Relevant PCMode processes are still running."

        foreach ($p in $running)
        {
            Write-Host "    $($p.ProcessName) PID=$($p.Id)"
        }
    } else
    {
        foreach ($p in $running)
        {
            Write-Host "Stopping $($p.ProcessName) PID=$($p.Id)"
            Stop-Process -Id $p.Id -Force
        }

        Pass "Relevant PCMode processes were stopped."
    }

    # -------------------------------------------------------------------------
    # 6. Clean native output
    # -------------------------------------------------------------------------

    Write-Step "6. Removing old driver build output"

    $x64 = Join-Path $driverDir "x64"

    if (Test-Path $x64)
    {
        Remove-Item `
            $x64 `
            -Recurse `
            -Force

        Pass "Deleted previous x64 build directory."
    } else
    {
        Pass "No previous x64 directory existed."
    }

    Require `
    (-not (Test-Path $x64)) `
        "Old native build output is gone."

    # -------------------------------------------------------------------------
    # 7. Rebuild
    # -------------------------------------------------------------------------

    Write-Step "7. Rebuilding native driver"

    Push-Location $driverDir

    try
    {
        & $msbuildExe `
            $project.FullName `
            /t:Rebuild `
            /m `
            /p:Configuration=Debug `
            /p:Platform=x64

        $buildExitCode = $LASTEXITCODE
    } finally
    {
        Pop-Location
    }

    Require `
    ($buildExitCode -eq 0) `
        "Native x64 Debug driver build succeeded."

    # -------------------------------------------------------------------------
    # 8. Find generated package
    # -------------------------------------------------------------------------

    Write-Step "8. Finding generated driver package"

    $infCandidates = @(
        Get-ChildItem `
            $driverDir `
            -Recurse `
            -File `
            -Filter "PCMode.Audio.VirtualEndpoint.inf" |
            Where-Object {
                $_.FullName -match '\\x64\\Debug\\PCMode\.Audio\.VirtualEndpoint\\'
            } |
            Sort-Object LastWriteTime -Descending
    )

    Require `
    ($infCandidates.Count -gt 0) `
        "Generated PCMode.Audio.VirtualEndpoint.inf exists."

    $inf = $infCandidates[0]
    $pkg = $inf.Directory.FullName

    Write-Host "Package:"
    Write-Host "    $pkg"

    $sys = Get-ChildItem `
        $pkg `
        -File `
        -Filter "PCMode.Audio.VirtualEndpoint.sys" |
        Select-Object -First 1

    $cat = Get-ChildItem `
        $pkg `
        -File `
        -Filter "*.cat" |
        Select-Object -First 1

    Require `
    ($null -ne $sys) `
        "Generated SYS exists."

    Require `
    ($null -ne $cat) `
        "Generated CAT exists."

    Require `
    (Test-Path $inf.FullName) `
        "Generated INF exists."

    Write-Host ""

    Get-ChildItem $pkg |
        Select-Object Name, Length, LastWriteTime |
        Format-Table -AutoSize

    # -------------------------------------------------------------------------
    # 9. Freshness
    # -------------------------------------------------------------------------

    Write-Step "9. Checking generated file freshness"

    $now = Get-Date

    foreach ($file in @($sys, $cat, $inf))
    {
        $age = $now - $file.LastWriteTime

        if ($age.TotalMinutes -le 10)
        {
            Pass "$($file.Name) was generated recently ($($file.LastWriteTime))."
        } else
        {
            Warn "$($file.Name) timestamp looks old: $($file.LastWriteTime)"
        }
    }

    # -------------------------------------------------------------------------
    # 10. Hash new SYS
    # -------------------------------------------------------------------------

    Write-Step "10. Hashing newly built driver"

    $builtHash = Get-FileHash `
        $sys.FullName `
        -Algorithm SHA256

    Pass "Calculated fresh driver SHA256."

    Write-Host ""
    Write-Host "Built SYS:"
    Write-Host "    $($sys.FullName)"

    Write-Host ""
    Write-Host "SHA256:"
    Write-Host "    $($builtHash.Hash)"

    # -------------------------------------------------------------------------
    # 11. Existing root device
    # -------------------------------------------------------------------------

    Write-Step "11. Checking currently installed PCMode device"

    $devices = Get-PCModeDevice

    if ($devices.Count -eq 0)
    {
        Warn "No ROOT\PCMODEAUDIO device is currently installed."
        $device = $null
    } elseif ($devices.Count -gt 1)
    {
        Fail "Multiple ROOT\PCMODEAUDIO devices exist."

        $devices |
            Format-Table Status, Class, FriendlyName, InstanceId

        throw "Duplicate PCMode root devices detected."
    } else
    {
        $device = $devices[0]

        Pass "Exactly one device with Hardware ID ROOT\PCMODEAUDIO exists."

        $device |
            Format-List Status, Class, FriendlyName, InstanceId

        if ($device.Status -eq "OK")
        {
            Pass "Installed PCMode device reports Status=OK."
        } else
        {
            Warn "Installed device reports Status=$($device.Status)."
        }
    }

    # -------------------------------------------------------------------------
    # 12. Install / update
    # -------------------------------------------------------------------------

    if ($Install)
    {

        Write-Step "12. Installing/updating freshly built driver"

        $devconExe = Find-DevCon

        Require `
        (-not [string]::IsNullOrWhiteSpace($devconExe)) `
            "DevCon x64 was found."

        Write-Host "DevCon:"
        Write-Host "    $devconExe"

        if ($device)
        {

            Write-Host ""
            Write-Host "Updating existing PCMode audio endpoint..."

            & $devconExe `
                update `
                $inf.FullName `
                "ROOT\PCMODEAUDIO"

            $devconExitCode = $LASTEXITCODE

            Require `
            ($devconExitCode -eq 0) `
                "DevCon updated PCMode audio endpoint."
        } else
        {

            Write-Host ""
            Write-Host "No PCMode audio endpoint exists."
            Write-Host "Creating exactly one root-enumerated endpoint..."

            & $devconExe `
                install `
                $inf.FullName `
                "ROOT\PCMODEAUDIO"

            $devconExitCode = $LASTEXITCODE

            Require `
            ($devconExitCode -eq 0) `
                "DevCon created and installed PCMode audio endpoint."
        }

        Write-Host ""
        Write-Host "Waiting for Windows to enumerate ROOT\PCMODEAUDIO..."

        $devices = @()

        for ($attempt = 1; $attempt -le 5; $attempt++)
        {

            # Ask Windows to rescan the PnP device tree using the DevCon
            # instance we already successfully used for installation.
            & $devconExe rescan | Out-Null

            Start-Sleep -Seconds 1

            $devices = Get-PCModeDevice

            Write-Host `
                "    Attempt $attempt/5: found $($devices.Count) PCMode device(s)"

            if ($devices.Count -eq 1)
            {
                break
            }
        }

        if ($devices.Count -eq 0)
        {

            Write-Host ""
            Write-Host "Get-PnpDevice did not find ROOT\PCMODEAUDIO."
            Write-Host "Checking directly with DevCon..."

            & $devconExe find "ROOT\PCMODEAUDIO*"

            Write-Host ""
            & $devconExe status "ROOT\PCMODEAUDIO*"

            Fail "ROOT\PCMODEAUDIO was installed by DevCon but is not visible through Get-PnpDevice."
            throw "PCMode device enumeration failed."
        }

        if ($devices.Count -gt 1)
        {

            Write-Host ""

            $devices |
                Format-Table `
                    Status,
                Class,
                FriendlyName,
                InstanceId `
                    -AutoSize

            Fail "Multiple ROOT\PCMODEAUDIO devices exist after installation."
            throw "Duplicate PCMode root devices detected."
        }

        $device = $devices[0]

        Pass "Exactly one PCMode root device exists after installation."

        $device |
            Format-List `
                Status,
            Class,
            FriendlyName,
            InstanceId

        Require `
        ($device.Status -eq "OK") `
            "PCMode device is healthy after installation."

        $device = $devices[0]

        $device |
            Format-List Status, Class, FriendlyName, InstanceId

        Require `
        ($device.Status -eq "OK") `
            "PCMode device is healthy after installation."
    } else
    {
        Write-Step "12. Driver installation skipped"

        Warn "Run with -Install to install/update the freshly built driver."
    }

    # -------------------------------------------------------------------------
    # 13. Inspect installed package / driver
    # -------------------------------------------------------------------------

    Write-Step "13. Checking installed driver"

    if (-not $device)
    {
        Warn "Installed-driver verification skipped because ROOT\PCMODEAUDIO does not exist."
    } else
    {
        try
        {
            $serviceProperty =
            Get-PnpDeviceProperty `
                -InstanceId $device.InstanceId `
                -KeyName DEVPKEY_Device_Service `
                -ErrorAction Stop

            $serviceName = $serviceProperty.Data

            Require `
            (-not [string]::IsNullOrWhiteSpace($serviceName)) `
                "PCMode device has an associated driver service."

            Write-Host "Driver service:"
            Write-Host "    $serviceName"

            $systemDriver =
            Get-CimInstance Win32_SystemDriver |
                Where-Object {
                    $_.Name -eq $serviceName
                } |
                Select-Object -First 1

            Require `
            ($null -ne $systemDriver) `
                "Driver service exists in Win32_SystemDriver."

            Write-Host ""

            $systemDriver |
                Format-List Name, State, StartMode, PathName

            if ($systemDriver.State -eq "Running")
            {
                Pass "Kernel driver service is running."
            } else
            {
                Warn "Kernel driver service state is '$($systemDriver.State)'."
            }

            $loadedPath =
            Normalize-DriverPath $systemDriver.PathName

            Require `
            (-not [string]::IsNullOrWhiteSpace($loadedPath)) `
                "Installed SYS path could be resolved."

            Write-Host "Installed SYS:"
            Write-Host "    $loadedPath"

            Require `
            (Test-Path $loadedPath) `
                "Installed SYS exists on disk."

            $installedHash =
            Get-FileHash `
                $loadedPath `
                -Algorithm SHA256

            Write-Host ""
            Write-Host "Built hash:"
            Write-Host "    $($builtHash.Hash)"

            Write-Host "Installed hash:"
            Write-Host "    $($installedHash.Hash)"

            if ($builtHash.Hash -eq $installedHash.Hash)
            {
                Pass "Installed SYS exactly matches freshly built SYS."
            } else
            {
                Fail "Installed SYS DOES NOT match freshly built SYS."

                Write-Host ""
                Write-Host "Windows is using a different driver binary."
                Write-Host "A reboot or Driver Store cleanup may be required."
            }

            try
            {
                $infProperty =
                Get-PnpDeviceProperty `
                    -InstanceId $device.InstanceId `
                    -KeyName DEVPKEY_Device_DriverInfPath `
                    -ErrorAction Stop

                $versionProperty =
                Get-PnpDeviceProperty `
                    -InstanceId $device.InstanceId `
                    -KeyName DEVPKEY_Device_DriverVersion `
                    -ErrorAction Stop

                $dateProperty =
                Get-PnpDeviceProperty `
                    -InstanceId $device.InstanceId `
                    -KeyName DEVPKEY_Device_DriverDate `
                    -ErrorAction Stop

                Write-Host ""
                Write-Host "Installed INF     : $($infProperty.Data)"
                Write-Host "Installed version : $($versionProperty.Data)"
                Write-Host "Installed date    : $($dateProperty.Data)"
            } catch
            {
                Warn "Could not read all installed driver metadata: $($_.Exception.Message)"
            }
        } catch
        {
            Fail "Installed-driver verification failed: $($_.Exception.Message)"
        }
    }

    # -------------------------------------------------------------------------
    # 14. Managed DriverClient
    # -------------------------------------------------------------------------

    if ($TestManaged)
    {

        Write-Step "14. Building/testing managed DriverClient"

        $clientProject = Join-Path `
            $Root `
            "app\src\PCMode.Audio.DriverClient\PCMode.Audio.DriverClient.csproj"

        $clientTests = Join-Path `
            $Root `
            "app\tests\PCMode.Audio.DriverClient.Tests\PCMode.Audio.DriverClient.Tests.csproj"

        Require `
        (Test-Path $clientProject) `
            "Managed DriverClient project exists."

        & dotnet build $clientProject

        $clientBuildExitCode = $LASTEXITCODE

        Require `
        ($clientBuildExitCode -eq 0) `
            "PCMode.Audio.DriverClient builds successfully."

        if (Test-Path $clientTests)
        {
            & dotnet test $clientTests

            $clientTestExitCode = $LASTEXITCODE

            Require `
            ($clientTestExitCode -eq 0) `
                "PCMode.Audio.DriverClient tests pass."
        } else
        {
            Warn "DriverClient test project was not found."
        }
    } else
    {
        Write-Step "14. Managed DriverClient tests skipped"

        Write-Host "Use -TestManaged to build and test DriverClient."
    }

    # -------------------------------------------------------------------------
    # 15. Final result
    # -------------------------------------------------------------------------

    Write-Step "15. RESULT"

    Write-Host "Driver source:"
    Write-Host "    $driverDir"

    Write-Host ""
    Write-Host "Fresh package:"
    Write-Host "    $pkg"

    Write-Host ""
    Write-Host "Fresh SYS:"
    Write-Host "    $($sys.FullName)"

    Write-Host ""
    Write-Host "Fresh SYS SHA256:"
    Write-Host "    $($builtHash.Hash)"

    Write-Host ""
    Write-Host "Failures : $script:Failures"
    Write-Host "Warnings : $script:Warnings"

    Write-Host ""

    if ($script:Failures -eq 0)
    {
        Write-Host "SUCCESS" -ForegroundColor Green

        if ($script:Warnings -eq 0)
        {
            Write-Host "Driver rebuild and verification completed without warnings."
        } else
        {
            Write-Host "Driver rebuild completed with $script:Warnings warning(s)." `
                -ForegroundColor Yellow
        }

        exit 0
    }

    Write-Host "FAILED" -ForegroundColor Red
    Write-Host "$script:Failures failure(s) detected."

    exit 1
} catch
{

    Write-Host ""
    Write-Host "============================================================"
    Write-Host "SCRIPT STOPPED" -ForegroundColor Red
    Write-Host "============================================================"

    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red

    Write-Host ""
    Write-Host "Failures : $script:Failures"
    Write-Host "Warnings : $script:Warnings"

    exit 1
}
