# 台電南部施工處 工安表單資訊化系統（重寫版）

2020 年舊版（ASP.NET MVC 5 + Dapper）的重寫。目標：**基礎平台 + 工安巡查紀錄表（Pr1）** 先行，
以泛用簽核流程引擎取代舊版每張表單各自複製的寬表與字串比對。

## 技術棧

| 項目 | 採用 |
|---|---|
| 框架 | ASP.NET Core 8 MVC（C#、Razor、可部署於 IIS） |
| 資料存取 | Dapper + Microsoft.Data.SqlClient（SQL Server 2016+） |
| 資料庫遷移 | DbUp（內嵌 `Migrations/Scripts/V###__*.sql`，依序執行） |
| 郵件 | MailKit（SMTP，開發模式只寫 log） |
| 測試 | xUnit |

## 方案結構

```
src/Taipower.Safety.Core            領域層（流程狀態機、模型、介面）— 無外部相依
src/Taipower.Safety.Infrastructure  Dapper 儲存庫、DbUp 遷移、郵件、legacy 匯入器
src/Taipower.Safety.Web             MVC 網站（Cookie 驗證、全域授權、Razor 畫面）
tests/Taipower.Safety.Tests         單元測試（流程狀態機、密碼雜湊）
tools/Taipower.Safety.DbTool        CLI：migrate / create-admin / import-legacy
```

## 安全性（對應舊版缺失與招標需求 5.1）

- **全站預設需登入**：`AuthorizeFilter` 全域套用，端點須明確 `[AllowAnonymous]`。舊版完全沒有任何檢查。
- **功能權限**：角色—功能對應（`RoleFeature`），控制器以 `[RequireFeature(...)]` 宣告；對應舊版 `Features` 的 Func_id，資料可 1:1 沿用。
- **CSRF**：全站 `AutoValidateAntiforgeryToken`。
- **SQL Injection**：全面參數化；排序欄位不再字串串接（舊版 6 處 `ORDER BY` 串接已移除）。
- **密碼**：PBKDF2-SHA256；可驗證舊版 `Base64(SHA512(Big5(pwd+salt)))`，登入時自動升級（`PasswordHasher`）。登入失敗鎖定。
- **祕密外移**：連線字串、SMTP 帳密、對外網址皆走設定（user-secrets／環境變數），不再寫死於原始碼。
- **稽核軌跡**：`AuditLog` 記錄登入、各項異動的成功/失敗、IP。
- **通知信連結**：指向表單頁，未登入者走正常登入 + ReturnUrl；不再把員工代號放進網址。

## 泛用簽核流程引擎

- 單張表單狀態集中於 `FormInstance`（+ `Pr1Form` 表頭 / `Pr1Item` 項目 / `WorkflowHistory` 歷程 / `Attachment`）。
  取代舊版 `Pr1` 一張約 90 欄的寬表與各關卡 `C3_audit_id1..3`、`C4_...` 等欄位。
- 關卡以 `(Stage, Position, Transit)` 建模（`Pr1Workflow`，純邏輯、可測）。
  舊版散落在控制器/DAO 的 `switch("C327")` 字串比對改為一張宣告式轉移表。
- 「誰領下一關」由 `AssigneeRule` + `IAssigneeResolver` 決定，集中舊版 `GetDispatchman`/`GetSir`/`GetMySupervisor`
  等查詢並加上 null 檢查；含 C6 長官鏈（領隊→工程副處長→工安副處長→處長）的去重跳層規則。
- 樂觀並行控制（`rowversion`），避免舊版「歷程字串經瀏覽器往返覆寫」造成的遺失。

### Pr1 流程（已驗證）

`開單(C1) → 工安組簽註(C3) → 經辦組處理改善(C4) → 工安組查證(C5) → 長官覆核(C6) → 結案/作廢`，
每一簽核階段皆支援：登記桌派課長、課長派經辦、經辦上呈（可跳關直送經理）、課長上呈（可代理經理）、
退回經辦/課長/登記桌/開單人、抽回，以及 C6 的同意/不同意結案、核可/不核可。

## 執行方式

### 1. 準備資料庫
```bash
# 建立 schema（不存在則自動建立資料庫）
dotnet run --project tools/Taipower.Safety.DbTool -- migrate \
  --db "Server=.;Database=TaipowerSafety;Integrated Security=true;TrustServerCertificate=true"

# 建立系統管理員帳號
dotnet run --project tools/Taipower.Safety.DbTool -- create-admin \
  --db "<conn>" --account admin --employee A00001 --name 系統管理員 --password "<密碼>"

# （選用）從舊資料庫匯入人員/基本資料/權限/歷史表單（匯入的 Pr1 單為唯讀）
dotnet run --project tools/Taipower.Safety.DbTool -- import-legacy \
  --db "<新庫conn>" --legacy "<舊 TaipowerReport 連線字串>"
```

### 2. 設定與啟動
連線字串／SMTP 以設定提供，正式環境請用最小權限帳號（`db_datareader`/`db_datawriter`），切勿用 `sa`：
```bash
export Database__ConnectionString="Server=.;Database=TaipowerSafety;Integrated Security=true;TrustServerCertificate=true"
dotnet run --project src/Taipower.Safety.Web
```

### 3. 測試
```bash
dotnet test
```

## 已完成 / 待辦

**已完成**：基礎平台（驗證、授權、組織/職務/角色/人員/權限維護、基本資料維護、稽核查詢）、
資料庫遷移與種子、legacy 匯入器（已以真實舊資料驗證：253 員工、253 帳號、123 張歷史單）、
Pr1 流程引擎與建單/待辦/簽核/查詢（C1–C6 全程已端對端驗證）、單元測試。

**待辦（後續）**：
- Pr1 項目的**照片上傳**與改善前後對照、C4/C5 逐項改善與查證結果輸入畫面。
- **罰單（Pr5）** 連動與承攬商通知信（本次範圍為基礎 + Pr1）。
- Pr2/Pr3/Pr4 其餘表單（可重用同一流程引擎）。
- 以 git 納入版本控管（舊版以每日壓縮 zip 管理）。

> 舊版詳細流程規格與缺失清單見開發過程產出的 `pr1-spec.md`（暫存目錄）。
