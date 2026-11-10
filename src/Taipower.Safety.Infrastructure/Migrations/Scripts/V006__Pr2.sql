-- Pr2 (工安查核紀錄追蹤表) detail. 與 Pr1 同屬多關卡簽核表單，
-- 流程狀態同樣存於 dbo.FormInstance (FormType='PR2')、歷程於 dbo.WorkflowHistory；
-- 本表只存表頭與各階段旗標。共用 SafetyWorkflow 狀態機與 AssigneeResolver。

CREATE TABLE dbo.Pr2Form (
    FormId                bigint        NOT NULL CONSTRAINT PK_Pr2Form PRIMARY KEY
                                        CONSTRAINT FK_Pr2Form_Instance REFERENCES dbo.FormInstance(FormId),
    CheckType             varchar(2)    NOT NULL,          -- SI / SM / SL（查核單位；沿用巡查單位代碼）
    CheckDate             date          NOT NULL,
    ProjectId             int           NOT NULL CONSTRAINT FK_Pr2Form_Project REFERENCES dbo.EngineeringProject(ProjectId),
    ContractorId          int           NOT NULL CONSTRAINT FK_Pr2Form_Contractor REFERENCES dbo.Contractor(ContractorId),
    LeaderId              varchar(6)    NOT NULL,          -- 查核領隊
    TeamMembers           nvarchar(200) NULL,
    ImproveGroupId        varchar(3)    NULL,              -- 建單建議督促組別

    InformGroupId         varchar(3)    NULL,              -- C3 決定督促組別
    ImproveDeadline       date          NULL,
    ImproveCompletedDate  date          NULL,
    IsJump                bit           NOT NULL CONSTRAINT DF_Pr2Form_Jump DEFAULT 0,
    IsProxy               bit           NOT NULL CONSTRAINT DF_Pr2Form_Proxy DEFAULT 0,

    SafetyOfficerId       varchar(6)    NULL,
    SafetyChiefId         varchar(6)    NULL,
    SafetyManagerId       varchar(6)    NULL,
    ImproveOfficerId      varchar(6)    NULL,
    ImproveChiefId        varchar(6)    NULL,
    ImproveManagerId      varchar(6)    NULL,
    VerifyOfficerId       varchar(6)    NULL,
    VerifyChiefId         varchar(6)    NULL,
    VerifyManagerId       varchar(6)    NULL
);

CREATE TABLE dbo.Pr2Item (
    ItemId             bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Pr2Item PRIMARY KEY,
    FormId             bigint         NOT NULL CONSTRAINT FK_Pr2Item_Form REFERENCES dbo.Pr2Form(FormId),
    Kind               tinyint        NOT NULL,            -- 0 優點 / 1 缺點 / 2 建議改善
    Ordinal            int            NOT NULL,
    Description        nvarchar(500)  NOT NULL,
    HazardId           int            NULL,
    DeficiencyTypeId   int            NULL,
    DeficiencyItemId   int            NULL,
    HazardLabel        nvarchar(300)  NULL,
    Improved           bit            NULL,
    NotImprovedReason  nvarchar(300)  NULL,
    ImprovedDate       date           NULL,
    Verified           bit            NULL,
    VerifyReason       nvarchar(300)  NULL,
    PenaltyTicketNo    nvarchar(30)   NULL                 -- link to Pr5
);
CREATE INDEX IX_Pr2Item_Form ON dbo.Pr2Item(FormId, Kind, Ordinal);

-- Pr2 功能權限（legacy Pr2 func id 不明，故新增編號；沿用 Pr1 的 100-108/199 編碼結構為 210-218/219）。
INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES (210, NULL, N'工安查核紀錄追蹤表', NULL, 210);
INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES
 (211, 210, N'開單',                  '/Pr2/Create', 211),
 (212, 210, N'工安組簽註意見-派單',    NULL,          212),
 (213, 210, N'工安組簽註意見',        NULL,          213),
 (214, 210, N'經辦組改善或處理-派單',  NULL,          214),
 (215, 210, N'經辦組改善或處理',      NULL,          215),
 (216, 210, N'工安組查證結果-派單',    NULL,          216),
 (217, 210, N'工安組查證結果',        NULL,          217),
 (218, 210, N'長官覆核批示',          NULL,          218),
 (219, 210, N'查詢',                  '/Pr2',        219);

-- 系統管理員（Role 9）補得新功能。
INSERT dbo.RoleFeature (RoleId, FeatureId)
SELECT 9, f.FeatureId FROM dbo.Feature f
WHERE f.FeatureId BETWEEN 210 AND 219
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleFeature rf WHERE rf.RoleId = 9 AND rf.FeatureId = f.FeatureId);
