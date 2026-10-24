# 配布用の YMM4FastPsdShape.ymme を作る（Windows の PowerShell で実行）
#   使い方: リポジトリのルートで  powershell -ExecutionPolicy Bypass -File tools\pack.ps1
#   出力:   artifacts\YMM4FastPsdShape.ymme
#
# .ymme は中身を zip にして拡張子を変えたもの。
# YMM4 のプラグインインストーラーは、ファイル名（拡張子を除く）をフォルダ名として user\plugin\ に展開し、
# zip 内の README.txt を「このプラグインについて」として表示する。
# ファイル名にバージョンを付けると更新のたびに別フォルダに入ってしまうので、ファイル名は固定にしている。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\YMM4FastPsdShape\YMM4FastPsdShape.csproj'
$out = Join-Path $root 'artifacts'
$stage = Join-Path $out 'stage'

dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました' }

$dll = Get-ChildItem -Path (Join-Path $root 'src\YMM4FastPsdShape\bin\Release') -Recurse -Filter 'YMM4FastPsdShape.dll' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { throw 'YMM4FastPsdShape.dll が見つかりません' }

# zip の中は YMM4FastPsdShape フォルダを1つ挟む
$pkg = Join-Path $stage 'YMM4FastPsdShape'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $pkg -Force | Out-Null
Copy-Item $dll.FullName $pkg
Copy-Item (Join-Path $root 'dist\README.txt') $pkg
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $pkg 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $pkg 'THIRD-PARTY-NOTICES.txt')

$zip = Join-Path $out 'YMM4FastPsdShape.zip'
$ymme = Join-Path $out 'YMM4FastPsdShape.ymme'
Remove-Item $zip, $ymme -ErrorAction SilentlyContinue
Compress-Archive -Path $pkg -DestinationPath $zip
Move-Item $zip $ymme
Remove-Item $stage -Recurse -Force
Write-Host "作成しました: $ymme"
