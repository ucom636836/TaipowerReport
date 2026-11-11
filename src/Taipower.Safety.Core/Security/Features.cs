namespace Taipower.Safety.Core.Security;

/// <summary>
/// Feature ids (legacy Features.Func_id, kept so existing role assignments migrate 1:1).
/// A role is granted features through RoleFeature; controllers declare the feature they need.
/// </summary>
public static class Features
{
    public const int Pr1 = 100;                 // 工安巡查紀錄表
    public const int Pr1Create = 101;           // 開單
    public const int Pr1SafetyDispatch = 102;   // 工安組簽註意見-派單
    public const int Pr1SafetyReview = 103;     // 工安組簽註意見
    public const int Pr1HandlerDispatch = 104;  // 經辦組改善或處理-派單
    public const int Pr1HandlerProcess = 105;   // 經辦組改善或處理
    public const int Pr1VerifyDispatch = 106;   // 工安組查證結果-派單
    public const int Pr1Verify = 107;           // 工安組查證結果
    public const int Pr1Approve = 108;          // 長官覆核批示
    public const int Pr1Query = 199;            // 查詢

    public const int Pr2 = 210;                 // 工安查核紀錄追蹤表
    public const int Pr2Create = 211;           // 開單
    public const int Pr2SafetyDispatch = 212;   // 工安組簽註意見-派單
    public const int Pr2SafetyReview = 213;     // 工安組簽註意見
    public const int Pr2HandlerDispatch = 214;  // 經辦組改善或處理-派單
    public const int Pr2HandlerProcess = 215;   // 經辦組改善或處理
    public const int Pr2VerifyDispatch = 216;   // 工安組查證結果-派單
    public const int Pr2Verify = 217;           // 工安組查證結果
    public const int Pr2Approve = 218;          // 長官覆核批示
    public const int Pr2Query = 219;            // 查詢

    public const int Pr3 = 220;                 // 各級主管走動管理紀錄暨追蹤表
    public const int Pr3Create = 221;           // 開單
    public const int Pr3InspectorConfirm = 222; // 巡查人確認
    public const int Pr3HandlingReview = 223;   // 經辦組簽註
    public const int Pr3SafetyReview = 224;     // 工安組查證/追蹤
    public const int Pr3Query = 229;            // 查詢

    public const int Pr4 = 230;                 // 承攬商工作場所工安抽查紀錄表
    public const int Pr4Create = 231;           // 開單（檢驗員）
    public const int Pr4Inspection = 232;       // 框線一 檢驗員簽核
    public const int Pr4Handling = 233;         // 框線二 經辦組查證
    public const int Pr4Query = 239;            // 查詢

    public const int Pr5 = 200;                 // 工程罰款管理
    public const int Pr5Create = 201;           // 開立罰單
    public const int Pr5Query = 299;            // 查詢

    public const int Admin = 600;               // 系統管理
    public const int AdminOrg = 601;            // 組織維護
    public const int AdminRankRole = 602;       // 職務角色維護
    public const int AdminEmployee = 603;       // 人員資料維護
    public const int AdminPermission = 604;     // 人員權限設定
    public const int AdminReference = 605;      // 缺失項目維護 / 基本資料
    public const int AdminAuditLog = 606;       // 操作紀錄查詢
}

public sealed record Feature(int FeatureId, int? ParentId, string Name, string? Path, int SortOrder);
