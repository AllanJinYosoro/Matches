# 关机/重启前脚本开发

Matches 会按设置中的顺序逐个执行所选脚本，并等待每个脚本结束。

- 支持 `.ps1`、`.cmd`、`.bat`、`.py` 和 `.exe`。
- 工作目录是脚本所在目录；PowerShell 可用 `$PSScriptRoot` 引用相邻文件。
- 退出码 `0` 表示成功并继续；非 `0`、文件缺失或启动失败都会取消关机/重启。
- 脚本应无需交互、可重复执行且能自行结束；调用外部程序后要检查 `$LASTEXITCODE`。
- 不要在此目录存放凭据、个人数据或需要提交的专有文件。

最小 PowerShell 模板：

```powershell
$ErrorActionPreference = 'Stop'

try {
    # 执行清理操作
    exit 0
}
catch {
    [Console]::Error.WriteLine($_)
    exit 1
}
```

在 Matches 的“设置 → 关机/重启前脚本”中添加并调整执行顺序。
