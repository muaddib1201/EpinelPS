# 本机 NIKKE 工作树规则

先阅读 `D:\Claude Lab\NIKKE\AGENTS.md`，它记录用户授权的多会话隔离、测试和统一部署流程。

- 本目录若属于当前会话已登记的独立 worktree，可继续使用；否则先运行该文件中的 Start-Session 命令并使用返回目录。
- 每个会话只修改自己的工作树。`D:\Claude Lab\NIKKE\src\EpinelPS` 原目录仅作参考。
- 集成基线是 `refs/heads/nikke/integration`，不得从旧上游 HEAD 开始而漏掉本地修复。
- 构建、测试、发布和安装统一使用 `D:\Claude Lab\NIKKE\.tools\collaboration\nikke_workflow.py`。先提交、integrate、sync、test、publish，再 seal；部署使用 deploy 持有全局锁。
- 安装前后检查线上程序哈希和最新集成提交；过期包重建。不得直接执行旧安装脚本或其封存副本。
- 线上运行目录和存档不属于测试输出；不得为测试创建指向线上目录的链接。

## GitHub 备份

- 用户已授权每次修改成功集成、同步和验证后，主动将完整集成提交及历史推送到 `https://github.com/muaddib1201/EpinelPS.git` 的 `nikke/integration` 分支，不再重复询问。
- Git 操作使用本会话登记的 `sourceDirectory`，推送 `refs/heads/nikke/integration` 的确切提交；保留官方 `origin`，不得强制推送或改写远程 `main`。
- 推送前检查新增历史中的存档、私钥和令牌；使用已授权的 HTTPS/Git Credential Manager 登录，推送后用 `ls-remote` 校验提交号。失败时明确报告未完成备份。
- 推送只备份已提交的源码，不替代测试、封存、统一部署或存档备份。
