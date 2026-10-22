# DeepPCB-Station · UI 闸门静态检查（上位机传统样式）
#
# 只做「能被机器判定」的部分；配色观感、信息密度仍需人眼验收。
# 用法： powershell -ExecutionPolicy Bypass -File scripts/check-ui-gate.ps1
# 退出码：0=通过，1=有违规

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ui = Join-Path $root 'src\DeepPCBStation'
$files = Get-ChildItem -Path $ui -Recurse -Filter *.xaml | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }

$violations = New-Object System.Collections.Generic.List[string]

foreach ($f in $files) {
    $text = Get-Content -Raw -Path $f.FullName
    $rel = $f.FullName.Substring($root.Length + 1)

    # 1) 圆角只允许 0/1/2（含多值写法逐项判断）
    foreach ($m in [regex]::Matches($text, 'CornerRadius="([^"]+)"')) {
        foreach ($part in $m.Groups[1].Value.Split(',')) {
            $v = 0.0
            if ([double]::TryParse($part.Trim(), [ref]$v)) {
                if ($v -gt 2) { $violations.Add("$rel :: CornerRadius=$($m.Groups[1].Value) > 2") }
            }
        }
    }

    # 2) 禁渐变 / 阴影 / 主题库痕迹
    foreach ($pat in @('<LinearGradientBrush', '<RadialGradientBrush', '<DropShadowEffect', '<BlurEffect', 'MaterialDesign', 'MahApps')) {
        if ($text.Contains($pat)) { $violations.Add("$rel :: 禁用元素 $pat") }
    }

    # 3) 可访问性：焦点框不得置空
    if ($text -match 'FocusVisualStyle" Value="\{x:Null\}"') {
        $violations.Add("$rel :: FocusVisualStyle=Null（闸门 13 禁止）")
    }
}

# 4) 骨架：主窗必须有菜单栏与状态栏
$main = Get-Content -Raw -Path (Join-Path $ui 'MainWindow.xaml')
if ($main -notmatch '<Menu ') { $violations.Add('MainWindow.xaml :: 缺少菜单栏') }
if ($main -notmatch 'Text="换型"') { $violations.Add('MainWindow.xaml :: 缺少状态栏换型格') }

if ($violations.Count -eq 0) {
    Write-Output "UI GATE: PASS ($($files.Count) xaml)"
    exit 0
}

Write-Output "UI GATE: FAIL ($($violations.Count))"
$violations | ForEach-Object { Write-Output "  - $_" }
exit 1
