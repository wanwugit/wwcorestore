<#
.SYNOPSIS
    部署前生产配置预检脚本 (preflight check)
    验证 appsettings.json 必填项、Redis 连通性、数据库连通性。

.DESCRIPTION
    本脚本不会修改任何文件，只读 + 连通性试探。
    web host 的 Program.cs 仅在 ASPNETCORE_ENVIRONMENT=Production 时做关键配置守卫，
    本脚本可在部署前 / CI 阶段提前发现空 Key，避免到启动时才崩。

.PARAMETER BaseDir
    解决方案根目录。默认脚本所在目录的父目录。

.PARAMETER SkipRedis
    跳过 Redis 连通性测试。

.PARAMETER SkipDb
    跳过数据库连通性测试。

.EXAMPLE
    .\scripts\preflight-check.ps1
    .\scripts\preflight-check.ps1 -SkipRedis
#>
[CmdletBinding()]
param(
    [string]$BaseDir = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipRedis,
    [switch]$SkipDb
)

$ErrorActionPreference = 'Stop'
$failures = @()
$warnings = @()

function Get-AppSettings {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
          $script:failures +=   "未找到 appsettings.json: $Path"
        return $null
    }
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    # 字符串感知地移除注释行/行尾注释，避免破坏 URL 中的 "//"
    $sb = New-Object System.Text.StringBuilder
    $inString = $false; $escape = $false; $i = 0
    while ($i -lt $raw.Length) {
        $c = $raw[$i]
        if ($escape) { [void]$sb.Append($c); $escape = $false; $i++; continue }
        if ($c -eq '\') { [void]$sb.Append($c); $escape = $true; $i++; continue }
        if ($inString) {
            if ($c -eq '"') { $inString = $false }
            [void]$sb.Append($c); $i++; continue
        }
        if ($c -eq '"') { $inString = $true; [void]$sb.Append($c); $i++; continue }
        if ($c -eq '/' -and ($i + 1) -lt $raw.Length -and $raw[$i + 1] -eq '/') {
            while ($i -lt $raw.Length -and $raw[$i] -ne "`n") { $i++ }
            continue
        }
        [void]$sb.Append($c); $i++
    }
    $cleaned = $sb.ToString()
    try {
        return $cleaned | ConvertFrom-Json
    } catch {
          $script:failures +=   "appsettings.json 解析失败 ($Path): $($_.Exception.Message)"
        return $null
    }
}

function Test-Key {
    param([object]$Obj, [string]$JsonPath, [int]$MinLen = 1, [string]$File)
    $parts = $JsonPath -split ':'
    $cur = $Obj
    foreach ($p in $parts) {
        if ($null -eq $cur -or -not ($cur.PSObject.Properties.Name -contains $p)) {
              $script:failures +=   "[$File] 缺少 $JsonPath"
            return
        }
        $cur = $cur.$p
    }
    $val = [string]$cur
    if ([string]::IsNullOrWhiteSpace($val)) {
          $script:failures +=   "[$File] $JsonPath 为空"
    } elseif ($val.Length -lt $MinLen) {
          $script:failures +=   "[$File] $JsonPath 长度不足 (实际 $($val.Length)，要求 >= $MinLen)"
    }
}

function Test-RedisConnect {
    param([string]$ConnStr)
    if ($ConnStr -match '^(?<host>[^,:]+):(?<port>\d+)') {
        $host = $Matches['host']; $port = [int]$Matches['port']
    } else {
          $script:failures +=   "Redis ConnectionString 解析失败: $ConnStr"
        return
    }
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $iar = $tcp.BeginConnect($host, $port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne(3000)) {
              $script:failures +=   "Redis 连接超时: ${host}:${port}"
            return
        }
        $tcp.EndConnect($iar); $tcp.Close()
        Write-Host "  [OK] Redis 可达: ${host}:${port}" -ForegroundColor Green
    } catch {
          $script:failures +=   "Redis 连接失败: ${host}:${port} -> $($_.Exception.Message)"
    }
}

function Test-DbConnect {
    param([string]$DbType, [string]$ConnStr)
    try {
        if ($DbType -eq 'SqlServer') {
            $conn = New-Object System.Data.SqlClient.SqlConnection($ConnStr)
            $conn.Open(); $conn.Close()
            Write-Host "  [OK] $DbType 连接成功" -ForegroundColor Green
        } elseif ($DbType -eq 'MySql') {
            if ($ConnStr -match 'server=([^;,]+);port=(\d+);') {
                $mhost = $Matches[1]; $mport = [int]$Matches[2]
            } else {
                  $script:warnings +=   "MySql 连接串无法解析 server/port: $ConnStr"
                return
            }
            $tcp = New-Object System.Net.Sockets.TcpClient
            $iar = $tcp.BeginConnect($mhost, $mport, $null, $null)
            if (-not $iar.AsyncWaitHandle.WaitOne(3000)) {
                  $script:failures +=   "MySql 连接超时: ${mhost}:${mport}"
                return
            }
            $tcp.EndConnect($iar); $tcp.Close()
            Write-Host "  [OK] MySql 端口可达: ${mhost}:${mport}" -ForegroundColor Green
        } else {
              $script:warnings +=   "未知 DbType: $DbType，跳过数据库测试"
        }
    } catch {
          $script:failures +=   "${DbType} 连接失败: $($_.Exception.Message)"
    }
}

Write-Host "`n=== 1. 检查 WebApi appsettings.json ===" -ForegroundColor Cyan
$webApiFile = Join-Path $BaseDir 'CoreCms.Net.Web.WebApi\appsettings.json'
$webApiCfg = Get-AppSettings $webApiFile
if ($webApiCfg) {
    Test-Key $webApiCfg 'JwtConfig:SecretKey' 16   $webApiFile
    Test-Key $webApiCfg 'JwtConfig:Issuer'    1    $webApiFile
    Test-Key $webApiCfg 'HangFire:Login'      1    $webApiFile
    Test-Key $webApiCfg 'HangFire:PassWord'   1    $webApiFile
    Test-Key $webApiCfg 'SwaggerConfig:UserName' 1 $webApiFile
    Test-Key $webApiCfg 'SwaggerConfig:PassWord'  1 $webApiFile

    if (-not $SkipRedis) {
        $redisConns = $webApiCfg.RedisConfig.ConnectionString
        Write-Host "  测试 Redis: $redisConns"
        Test-RedisConnect $redisConns
    } else { Write-Host "  [SKIP] Redis 连通性测试" -ForegroundColor DarkGray }

    if (-not $SkipDb) {
        $dbType = $webApiCfg.ConnectionStrings.DbType
        # 仓库内 ConnectionStrings 子键以 DbType 命名（如 SqlConnection / MySql），不是 DbConnectionString
        $dbConns = if ($dbType -and $webApiCfg.ConnectionStrings.$dbType) { $webApiCfg.ConnectionStrings.$dbType } else { $webApiCfg.ConnectionStrings.DbConnectionString }
        if ($dbType -and $dbConns) {
            if ($dbType -eq 'SqlServer' -and $dbConns -notmatch 'MultipleActiveResultSets=true') {
                  $script:warnings +=   "SqlServer 连接串建议包含 MultipleActiveResultSets=true (AGENTS.md 建议)"
            }
            Write-Host "  测试 $dbType 数据库"
            Test-DbConnect $dbType $dbConns
        } else {
              $script:failures +=   "WebApi ConnectionStrings:DbType 或连接串为空"
        }
    } else { Write-Host "  [SKIP] 数据库连通性测试" -ForegroundColor DarkGray }
}

Write-Host "`n=== 2. 检查 Admin appsettings.json ===" -ForegroundColor Cyan
$adminFile = Join-Path $BaseDir 'CoreCms.Net.Web.Admin\appsettings.json'
$adminCfg = Get-AppSettings $adminFile
if ($adminCfg) {
    Test-Key $adminCfg 'JwtConfig:SecretKey' 16  $adminFile
    Test-Key $adminCfg 'JwtConfig:Issuer'    1   $adminFile

    if (-not $SkipRedis -and $adminCfg.RedisConfig.ConnectionString) {
        $redisConns = $adminCfg.RedisConfig.ConnectionString
        Write-Host "  测试 Redis: $redisConns"
        Test-RedisConnect $redisConns
    } elseif (-not $SkipRedis) {
          $script:warnings +=   "Admin appsettings 缺少 RedisConfig:ConnectionString"
    }

    if (-not $SkipDb) {
        $dbType = $adminCfg.ConnectionStrings.DbType
        $dbConns = if ($dbType -and $adminCfg.ConnectionStrings.$dbType) { $adminCfg.ConnectionStrings.$dbType } else { $adminCfg.ConnectionStrings.DbConnectionString }
        if ($dbType -and $dbConns) {
            Write-Host "  测试 $dbType 数据库"
            Test-DbConnect $dbType $dbConns
        } else {
              $script:warnings +=   "Admin 缺少 ConnectionStrings:DbType 或连接串"
        }
    }
}

Write-Host "`n=== 3. 检查 Distribution 门控 ===" -ForegroundColor Cyan
if ($webApiCfg -and $webApiCfg.Distribution) {
    $gate = $webApiCfg.Distribution.CommissionSettleEnabled
    $pp = $webApiCfg.Distribution.CommissionProtectionPeriodDays
    Write-Host ("  CommissionSettleEnabled = {0}" -f $gate)
    Write-Host ("  CommissionProtectionPeriodDays = {0}" -f $pp)
    if ($gate -eq '1' -or $gate -eq 'true') {
          $script:warnings +=   "CommissionSettleEnabled 已开启。请确认 docs/design/05 迁移 003/004/005 已应用到生产 DB 且 staging 已冒烟。"
        if ($pp -and [int]$pp -gt 0) {
            Write-Host "  保护期模式：FinishOrder 仅设 expectedSettleTime，Hangfire 每小时扫描到期结算" -ForegroundColor Yellow
        }
    } else {
        Write-Host "  降级模式 (CommissionSettleEnabled=0)，分销佣金功能未启用，可安全上线" -ForegroundColor Green
    }
} else {
      $script:warnings +=   "WebApi appsettings 缺少 Distribution 节，按默认禁用处理"
}

Write-Host "`n=== 汇总 ===" -ForegroundColor Cyan
if ($warnings.Count -gt 0) {
    Write-Host "警告 $($warnings.Count) 项:" -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host "  WARN: $_" -ForegroundColor Yellow }
}
if ($failures.Count -gt 0) {
    Write-Host "失败 $($failures.Count) 项:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  FAIL: $_" -ForegroundColor Red }
    Write-Host "`n>>> 预检未通过，请修复以上失败项后再部署 <<<" -ForegroundColor Red
    exit 1
} else {
    Write-Host "`n>>> 预检通过 <<<" -ForegroundColor Green
    exit 0
}



