<#
  Создаёт ключ цифровой подписи WinTweak (запускать один раз, на своём компьютере).

  Запуск: правый клик по файлу → «Выполнить с помощью PowerShell»
  или в PowerShell:  powershell -ExecutionPolicy Bypass -File tools\New-SigningCert.ps1

  Что делает:
    1. Создаёт самоподписанный сертификат подписи кода «CN=rentice1337» на 10 лет.
    2. Сохраняет его на Рабочий стол в папку WinTweak-signing:
         WinTweak-signing.pfx    — ключ с паролем. ХРАНИТЕ В НАДЁЖНОМ МЕСТЕ, никому не отправляйте;
         WinTweak-signing.cer    — открытая часть (её можно показывать всем);
         SIGN_PFX_BASE64.txt     — ключ для секрета GitHub;
         SIGN_PFX_PASSWORD.txt   — пароль для секрета GitHub.
    3. Если установлен GitHub CLI (gh) — предлагает сразу добавить оба секрета в репозиторий.
  Ключ не остаётся в хранилище сертификатов Windows — только в файле .pfx.
#>
$ErrorActionPreference = 'Stop'
$subject = 'CN=rentice1337'
$repo = 'rentice1337/WinTweaker'
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'WinTweak-signing'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -FriendlyName 'WinTweak code signing' `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10)

# случайный пароль для файла ключа
$bytes = New-Object byte[] 24
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes)
$secure = ConvertTo-SecureString $password -AsPlainText -Force

$pfx = Join-Path $out 'WinTweak-signing.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $out 'WinTweak-signing.cer') | Out-Null
$b64File = Join-Path $out 'SIGN_PFX_BASE64.txt'
$pwdFile = Join-Path $out 'SIGN_PFX_PASSWORD.txt'
[IO.File]::WriteAllText($b64File, [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx)))
[IO.File]::WriteAllText($pwdFile, $password)

# ключ живёт только в .pfx — из хранилища Windows убираем
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey

Write-Host ''
Write-Host 'Готово. Файлы в папке:' $out -ForegroundColor Green
Write-Host 'Отпечаток сертификата:' $cert.Thumbprint
Write-Host ''

$added = $false
if (Get-Command gh -ErrorAction SilentlyContinue) {
    $answer = Read-Host "Добавить секреты в $repo через GitHub CLI? (д/н)"
    if ($answer -match '^(д|y)') {
        Get-Content $b64File -Raw | gh secret set SIGN_PFX_BASE64 -R $repo
        Get-Content $pwdFile -Raw | gh secret set SIGN_PFX_PASSWORD -R $repo
        $added = $true
        Write-Host 'Секреты добавлены.' -ForegroundColor Green
    }
}
if (-not $added) {
    Write-Host 'Добавьте два секрета вручную:' -ForegroundColor Yellow
    Write-Host "  github.com/$repo → Settings → Secrets and variables → Actions → New repository secret"
    Write-Host '  SIGN_PFX_BASE64   = содержимое SIGN_PFX_BASE64.txt'
    Write-Host '  SIGN_PFX_PASSWORD = содержимое SIGN_PFX_PASSWORD.txt'
}
Write-Host ''
Write-Host 'После этого удалите оба .txt, а WinTweak-signing.pfx сохраните в надёжном месте (резервная копия).' -ForegroundColor Yellow
Write-Host 'Если ключ потеряется, подпись новых версий сменится — пользователи увидят другой отпечаток.'
Read-Host 'Нажмите Enter, чтобы закрыть'
