# 开发与验证脚本目录

已建立 [verify.mjs](verify.mjs)，从仓库根目录运行：

```sh
node scripts/verify.mjs --scope docs
node scripts/verify.mjs --scope backend
node scripts/verify.mjs --scope frontend
node scripts/verify.mjs --scope all
```

省略参数等于 all。docs 检查标题、UTF-8、换行、围栏及本地链接；backend 运行构建、测试和离线迁移SQL一致性检查；frontend 运行类型检查、测试及构建。需要先具备 .NET 10、Node、前端本地依赖，并执行 `dotnet tool restore` 还原本地EF工具，验证入口不自动安装依赖。

[verify-migrations.mjs](verify-migrations.mjs) 只把独立设计时工厂生成的SQL读入内存，与已交付幂等脚本比较，不连接数据库、不覆盖交付文件。

代码修改自动运行适合影响范围的检查；根工具、公共合同及跨模块改动覆盖 all。检查不能替代真实供应商查询、真实数据库或人工验收。

脚本不得隐式删除文件、修改凭据、迁移数据库、操作 Git 或安装全局依赖。测试清理也应遵守[根规则](../AGENTS.md)中的约束。
