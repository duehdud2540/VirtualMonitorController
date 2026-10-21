$ErrorActionPreference = 'Stop'
try {
    if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') {
        throw '이 배포본은 Intel/AMD Windows x64 PC 전용입니다.'
    }
    if ([Environment]::OSVersion.Version.Build -lt 22000) { throw '이 배포본은 Windows 11 이상을 대상으로 합니다.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $elevated = Start-Process -FilePath "$PSHOME\powershell.exe" -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath)) -Verb RunAs -Wait -PassThru
        exit $elevated.ExitCode
    }
    $driverPath = Join-Path $PSScriptRoot 'Driver'
    $infPath = Join-Path $driverPath 'MttVDD.inf'
    foreach ($name in @('MttVDD.inf', 'MttVDD.dll', 'mttvdd.cat', 'vdd_settings.xml', 'SignPath.cer')) {
        if (-not (Test-Path -LiteralPath (Join-Path $driverPath $name))) { throw "드라이버 파일이 없습니다: $name. ZIP 전체를 먼저 압축 해제하세요." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'App\VirtualMonitor.exe'))) { throw 'App 폴더를 포함한 ZIP 전체를 압축 해제하세요.' }
    Write-Host 'MttVDD 드라이버와 가상 모니터 1개를 설치합니다.'
    Write-Host '이미 설치된 장치는 재사용하며 기존 설정은 덮어쓰지 않습니다.'
    Write-Host '테스트 서명 모드와 보안 부팅은 변경하지 않습니다.'

    Add-Type -AssemblyName System.Security
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2((Join-Path $driverPath 'SignPath.cer'))
    $catalog = New-Object System.Security.Cryptography.Pkcs.SignedCms
    $catalog.Decode([IO.File]::ReadAllBytes((Join-Path $driverPath 'mttvdd.cat')))
    $catalog.CheckSignature($true)
    $expectedPublisher = '3CF8CF26D8BA266C3A483AB7D26D4A818E317D76'
    if ($certificate.Thumbprint -ne $expectedPublisher -or $catalog.SignerInfos[0].Certificate.Thumbprint -ne $expectedPublisher) {
        throw '포함된 드라이버의 게시자 인증서가 예상한 SignPath Foundation 인증서와 다릅니다.'
    }

    # Query failures must stop installation instead of creating a duplicate device.
    $existing = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object { $_.Class -eq 'Display' } | ForEach-Object {
        $ids = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction Stop).Data
        if (@($ids | Where-Object { $_ -ieq 'Root\MttVDD' -or $_ -ieq 'MttVDD' }).Count -gt 0) { $_ }
    })
    if ($existing.Count -gt 0) {
        Write-Host '기존 MttVDD 장치를 찾았습니다. 장치를 추가하지 않고 드라이버 등록 상태를 확인합니다.'
        $existing | Format-Table FriendlyName, Status, InstanceId

    }

    if (-not (Test-Path -LiteralPath "Cert:\LocalMachine\TrustedPublisher\$expectedPublisher")) {
        Write-Host '드라이버 설치를 위해 SignPath Foundation 게시자 인증서를 이 PC의 신뢰할 수 있는 게시자에 등록합니다.'
        Write-Host "인증서 지문: $expectedPublisher"
        Write-Host '루트 인증서 저장소는 변경하지 않습니다.'
        $answer = Read-Host '게시자 등록과 드라이버 설치를 진행하려면 Y를 입력하세요'
        if ($answer -ine 'Y') { throw '사용자가 설치를 취소했습니다.' }
        Import-Certificate -FilePath (Join-Path $driverPath 'SignPath.cer') -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
    }

    foreach ($directory in @('C:\VirtualDisplayDriver', 'C:\IddSampleDriver')) {
        if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
        $settings = Join-Path $directory 'vdd_settings.xml'
        if (-not (Test-Path -LiteralPath $settings)) { Copy-Item -LiteralPath (Join-Path $driverPath 'vdd_settings.xml') -Destination $settings }
    }
    & "$env:WINDIR\System32\pnputil.exe" /add-driver $infPath /install
    $packageResult = $LASTEXITCODE
    if ($packageResult -notin @(0, 3010)) { throw "드라이버 패키지 등록 실패: $packageResult. 서명 오류라면 공식 MttVDD 설치 방법을 확인하세요. 보안 설정은 자동으로 낮추지 않습니다." }

    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class VirtualMonitorInstaller {
    [StructLayout(LayoutKind.Sequential)] struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint Node; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid cls, IntPtr parent);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid cls, string description, IntPtr parent, uint flags, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref DeviceInfo info, uint property, byte[] data, uint size);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DeviceInfo info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr parent, string hardwareId, string inf, uint flags, out bool reboot);
    static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public static bool Install(string inf) {
        Guid cls = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        IntPtr set = SetupDiCreateDeviceInfoList(ref cls, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var info = new DeviceInfo { Size = (uint)Marshal.SizeOf(typeof(DeviceInfo)) };
            Check(SetupDiCreateDeviceInfo(set, "MttVDD", ref cls, "Virtual Display Driver", IntPtr.Zero, 1, ref info));
            byte[] ids = Encoding.Unicode.GetBytes("Root\\MttVDD\0\0");
            Check(SetupDiSetDeviceRegistryProperty(set, ref info, 1, ids, (uint)ids.Length));
            Check(SetupDiCallClassInstaller(0x19, set, ref info));
            bool reboot;
            Check(UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, "Root\\MttVDD", inf, 0, out reboot));
            return reboot;
        } finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
"@
    $restart = $false
    if ($existing.Count -eq 0) { $restart = [VirtualMonitorInstaller]::Install($infPath) }
    if (-not $restart -and $packageResult -ne 3010) {
        $healthy = $false
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            $devices = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object { $_.Class -eq 'Display' })
            foreach ($device in $devices) {
                $ids = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction Stop).Data
                if (@($ids | Where-Object { $_ -ieq 'Root\MttVDD' -or $_ -ieq 'MttVDD' }).Count -eq 0) { continue }
                $problem = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction Stop).Data
                if ($problem -in @(0, 22)) { $healthy = $true }
            }
            if ($healthy) { break }
            Start-Sleep -Milliseconds 250
        }
        if (-not $healthy) { throw '드라이버 등록 후 정상 장치 상태를 확인하지 못했습니다. 장치 관리자의 오류 코드 또는 재부팅 필요 여부를 확인하세요.' }
    }
    if ($restart -or $packageResult -eq 3010) { Write-Host '설치가 완료되었습니다. Windows를 재시작한 뒤 앱을 실행하세요.' }
    else { Write-Host '설치가 완료되었습니다. App\VirtualMonitor.exe를 실행하세요.' }
    exit 0
} catch {
    Write-Host "설치 실패: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host '등록 도중 실패한 장치가 남아 있을 수 있습니다. 오류 메시지와 장치 관리자 상태를 확인하세요.'
    Read-Host 'Enter 키를 누르면 종료합니다' | Out-Null
    exit 1
}

