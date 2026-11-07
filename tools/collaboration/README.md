# NIKKE 多会话协作

已采用“独立工作树开发和测试，Git 集成后构建，统一加锁部署”。当前线上目录和日常启动方式不变。

## 新会话和已有会话继续工作

在项目管理目录执行（每个会话使用自己的完整 ID）：

```powershell
& 'D:\Claude Lab\NIKKE\.tools\collaboration\Start-Session.ps1' -SessionId '<会话 ID>'
```

返回 `sourceDirectory` 和 `workspaceDirectory`。所有源码命令指定前者为工作目录。

- 源码：`.worktrees\<会话 ID>\src\EpinelPS`。
- 测试静态数据和合成存档：`.worktrees\<会话 ID>\Server and Server selector`。这是文件副本，不是线上目录的链接。
- 构建的 bin、obj、测试数据库以及 `.nikke` 发布输出都在自己的工作树。
- 相同 ID 复用原工作树，保留未完成修改；不同 ID 不共用工作区。
- 新会话从包含本地所有修复的 `nikke/integration` 创建。不要从旧上游 main 或原目录的旧 HEAD 直接建树。
- 原生工作树接口在本次设置中未返回结果，因此采用项目内 Git 后备方式；这些目录不依赖应用的工作树附件。后续会话由项目 AGENTS.md 引导进入自己的目录。

## 提交、合并和测试

下面 `python ...` 的固定脚本路径是 `D:\Claude Lab\NIKKE\.tools\collaboration\nikke_workflow.py`。

1. 在自己的源码目录完成修改并提交，只提交本任务相关文件。
2. `python <脚本路径> integrate --worktree '<自己的源码目录>'`：在锁内合并到集成引用。非冲突修改会合并，冲突会停止，保留双方工作树。
3. `python <脚本路径> sync --worktree '<自己的源码目录>'`：工作树须干净，只快进到最新集成引用。
4. `python <脚本路径> test --worktree '<自己的源码目录>' --suite all`：逐项目构建和回归测试，记录 `tests.json`。可指定相关项目，如 `CounselAffinityRegression LostSectorRewardRegression`。

原始 `src\EpinelPS` 保留已有文件、HEAD 和暂存状态，仅作为参考。MSBuild 会拒绝在这个共享 checkout 编译；测试及新修改必须使用自己的工作树。

## 构建和部署

```powershell
python <脚本路径> publish --worktree '<自己的源码目录>'
python <脚本路径> seal --worktree '<自己的源码目录>' --build '<build.json>' --tests '<tests.json>' --installer '<自己的 .nikke 中的 install.ps1>'
python <脚本路径> check-release --manifest '<release-xxx.json>'
python <脚本路径> deploy --manifest '<release-xxx.json>'
```

`publish` 只接受最新集成提交和干净工作树；记录全部发布文件的哈希。`seal` 核对构建和测试是否对应同一提交和源文件，封存安装脚本、证据和全部发布文件。`check-release` 不执行安装。

安装脚本必须先检查游戏已退出，备份程序、相关资源与存档，再替换明确列出的程序或资源，重启并检查健康和存档保留，失败恢复程序与资源。不得把测试存档复制到线上。封存前审查此脚本，并遵守当前用户对安装和重启的授权。

`deploy` 持有同一个操作系统部署锁和集成锁直到安装脚本及其回滚结束。它在锁内重新核对：线上程序版本、部署序号、最新集成提交、源码、安装脚本、构建和测试证据、整个发布目录的文件清单及哈希。任一不一致即停止，重新同步、测试和构建，不修改哈希绕过检查。

每次开始执行安装脚本前都会更新持久化部署序号，因此仅更新资源、数据或安装失败也会使旧清单失效。封存清单本身也在同样的两把锁内读取当前版本和序号。

安装子进程会继承并持有独占文件句柄；即使 Python 调度进程被终止，安装和回滚仍受锁保护。子进程在启动服务器前清除句柄的可继承标志，服务器不会长期占住部署锁。Windows 的这一行为使用 [CreateFile 文件共享语义](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)。

锁位于 `.tools\collaboration\state`。锁文件保留是正常现象；真正的锁由操作系统持有，持有者全部退出后释放。不要通过删除锁文件来“解锁”。等待超过 45 秒会退出，可稍后重试。

旧任务的安装、部署、等待安装入口已经停用；原文和哈希保存在 `legacy-installers` 和 `retired-installers.json`，只用于审查和恢复，不直接执行。新发布不再使用它们。

## 查询和验证

```powershell
python <脚本路径> status
python 'D:\Claude Lab\NIKKE\.tools\collaboration\test_workflow.py'
```

本次冲突核对记录：`.tools\concurrency-20261108\conflict-audit.json`。它确认当前部署程序逐字节包含受测程序集，850 个生产文件与合并快照一致，咨询及魔方奖励 19 组针对性检查和角色名接口通过。

此流程防止遵循入口的会话互相覆盖；不能阻止人为绕过入口直接写文件。已结束会话无需重开，继续工作时先运行 Start-Session 即可。
