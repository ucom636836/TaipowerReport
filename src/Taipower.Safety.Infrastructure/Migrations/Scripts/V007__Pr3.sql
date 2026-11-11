-- Pr3 (各級主管走動管理紀錄暨追蹤表) detail. 多關卡簽核，但關卡圖與 Pr1/Pr2 不同：
-- 巡查人起單 → 經辦組(S2) → 工安組(S3) → 結案/持續追蹤迴圈。流程狀態存於 dbo.FormInstance
-- (FormType='PR3'，CurrentStep 存 Pr3 專屬步碼；Revision 存重會次數 Pr3_retimes)、歷程於 dbo.WorkflowHistory。
-- 本表存表頭、旗標、各區塊已簽核人與持續追蹤迴圈次數。

CREATE TABLE dbo.Pr3Form (
    FormId             bigint        NOT NULL CONSTRAINT PK_Pr3Form PRIMARY KEY
                                     CONSTRAINT FK_Pr3Form_Instance REFERENCES dbo.FormInstance(FormId),
    InspectorId        varchar(6)    NOT NULL,          -- 巡查人 Pr3_Iman
    InspectDate        date          NOT NULL,          -- 巡查日期 Pr3_Sdate
    InspectTime        varchar(20)   NULL,              -- 巡查時間 Pr3_Stime
    Attendants         nvarchar(200) NULL,              -- 會同人員 Pr3_attman
    Location           nvarchar(200) NULL,              -- 巡查地點 Pr3_locus
    Target             nvarchar(200) NULL,              -- 受巡人數/設備 Pr3_target
    ProjectId          int           NOT NULL CONSTRAINT FK_Pr3Form_Project REFERENCES dbo.EngineeringProject(ProjectId),
    ContractorId       int           NOT NULL CONSTRAINT FK_Pr3Form_Contractor REFERENCES dbo.Contractor(ContractorId),
    GroupId            varchar(3)    NOT NULL,          -- 辦理組別 Pr3_Group_id（S2 動態路由）
    ImproveDeadline    date          NULL,              -- 限期改善日 Pr3_Edate

    IsJump             bit           NOT NULL CONSTRAINT DF_Pr3Form_Jump DEFAULT 0,
    Archive            int           NOT NULL CONSTRAINT DF_Pr3Form_Archive DEFAULT 0,  -- 持續追蹤迴圈次數 Pr3_Archive

    HandlingOfficerId  varchar(6)    NULL,              -- S2 經辦組
    HandlingChiefId    varchar(6)    NULL,
    HandlingManagerId  varchar(6)    NULL,
    SafetyOfficerId    varchar(6)    NULL,              -- S3 工安組
    SafetyChiefId      varchar(6)    NULL,
    SafetyManagerId    varchar(6)    NULL
);

CREATE TABLE dbo.Pr3Item (
    ItemId             bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Pr3Item PRIMARY KEY,
    FormId             bigint         NOT NULL CONSTRAINT FK_Pr3Item_Form REFERENCES dbo.Pr3Form(FormId),
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
    VerifyReason       nvarchar(300)  NULL
);
CREATE INDEX IX_Pr3Item_Form ON dbo.Pr3Item(FormId, Kind, Ordinal);

-- Pr3 功能權限（legacy Pr3 func id 不明，故新增編號 220-229）。
INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES (220, NULL, N'各級主管走動管理紀錄暨追蹤表', NULL, 220);
INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES
 (221, 220, N'開單',               '/Pr3/Create', 221),
 (222, 220, N'巡查人確認',          NULL,          222),
 (223, 220, N'經辦組簽註',          NULL,          223),
 (224, 220, N'工安組查證/追蹤',      NULL,          224),
 (229, 220, N'查詢',               '/Pr3',        229);

-- 系統管理員（Role 9）補得新功能。
INSERT dbo.RoleFeature (RoleId, FeatureId)
SELECT 9, f.FeatureId FROM dbo.Feature f
WHERE f.FeatureId BETWEEN 220 AND 229
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleFeature rf WHERE rf.RoleId = 9 AND rf.FeatureId = f.FeatureId);
