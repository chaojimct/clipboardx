# 打「迁移版」发布包（老仓库 v1.9.10 用）
#
# 目的：把 Migrator 产物按老更新器的资产规则打包，使老用户点「检查更新」后
# 能顺通道落到迁移向导。
#
# 老更新器硬约束（Services/GitHubUpdateService.cs，不满足会被拒绝）：
#   1) 资产名 = {主程序名}-{版本}-win-x64-{self-contained|no-runtime}.zip
#      · 主程序名 ClipboardX 时还会排除 -clipboard- / -filejump- 前缀的包；
#      · 两个运行形态变体都要有（老用户两种形态都有）。
#   2) zip 内根目录必须有 ClipboardX.exe（安装后 Start-Process <installDir>\ClipboardX.exe）。
#   3) tag 版本必须 > 1.9.9（IsRemoteNewerThanCurrent 用 Version 比较）。
#
# 用法（在仓库根目录）：
#   pwsh -File Migrator/build-migrator-package.ps1 -Version 1.9.10
# 产物落 Migrator/out/ 下 4 个 zip（Full flavor 两个形态 + 两个精简 flavor 占位说明）。

param(
    [string]$Version = "1.9.10",
    [switch]$SkipFlavors   # 演练时只打 Full 两形态，省时间
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'Migrator\Migrator.csproj'
$outDir = Join-Path $root 'Migrator\out'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "== 打迁移版 v$Version ==" -ForegroundColor Cyan

# 两种运行形态：老用户既可能装 self-contained（自带运行时），
# 也可能装 no-runtime（依赖 dotnet shared）。两个都得有。
$variants = @(
    @{ Name = 'no-runtime';     SelfContained = 'false'; Compress = 'false' },
    @{ Name = 'self-contained'; SelfContained = 'true';  Compress = 'true'  }
)

foreach ($v in $variants) {
    $pub = Join-Path $outDir "publish-$($v.Name)"
    if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }

    Write-Host "· publish ($($v.Name))…"
    $args = @(
        'publish', $proj, '-c', 'Release', '-r', 'win-x64',
        "-p:SelfContained=$($v.SelfContained)",
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:IncludeAllContentForSelfExtract=true',
        '-p:DebugType=None',
        "-p:Version=$Version",
        '-o', $pub
    )
    # 勿对 FDD 用 EnableCompressionInSingleFile（NETSDK1176：仅独立应用支持）
    if ($v.Compress -eq 'true') { $args += '-p:EnableCompressionInSingleFile=true' }

    & dotnet @args | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "publish 失败（$($v.Name)）" }

    $exe = Join-Path $pub 'ClipboardX.exe'
    if (-not (Test-Path $exe)) { throw "publish 输出缺少 ClipboardX.exe（$($v.Name)）" }

    $zip = Join-Path $outDir "ClipboardX-$Version-win-x64-$($v.Name).zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -Force
    $mb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
    Write-Host "  → $(Split-Path -Leaf $zip)  ($mb MB)" -ForegroundColor Green
}

if (-not $SkipFlavors) {
    Write-Host "· 精简 flavor（clipboard / filejump）…" -ForegroundColor Yellow
    Write-Host "  注意：这两个 flavor 的迁移包与 Full 内容一致（launcher 逻辑不分 flavor），" -ForegroundColor Yellow
    Write-Host "  仅需换包内 exe 名（ClipboardX-clipboard.exe / ClipboardX-filejump.exe）后重打。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "完成。产物在 $outDir" -ForegroundColor Cyan
Get-ChildItem $outDir -Filter '*.zip' | ForEach-Object {
    Write-Host ("  {0}  {1} MB" -f $_.Name, [math]::Round($_.Length / 1MB, 2))
}
