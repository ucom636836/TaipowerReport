-- Pr4 (南部施工處承攬商工作場所工安抽查紀錄表，契約附件1-4)。
-- legacy 從未實作，本表依需求報告書規格從零設計。多關卡簽核（條件分支）：
--   框線一 檢驗員 → 課長 → 經理；需改善→框線二 經辦組(登記桌→經辦→課長→經理)→結案；免改善→直接結案。
-- 流程狀態存於 dbo.FormInstance (FormType='PR4'；Revision 存重會次數)、歷程於 dbo.WorkflowHistory。

CREATE TABLE dbo.Pr4Form (
    FormId               bigint        NOT NULL CONSTRAINT PK_Pr4Form PRIMARY KEY
                                       CONSTRAINT FK_Pr4Form_Instance REFERENCES dbo.FormInstance(FormId),
    SpotCheckDate        date          NOT NULL,          -- 抽查日期
    ProjectId            int           NOT NULL CONSTRAINT FK_Pr4Form_Project REFERENCES dbo.EngineeringProject(ProjectId),
    ContractorId         int           NOT NULL CONSTRAINT FK_Pr4Form_Contractor REFERENCES dbo.Contractor(ContractorId),
    Location             nvarchar(200) NULL,              -- 抽查地點
    ArriveTime           varchar(20)   NULL,              -- 檢驗員到場
    LeaveTime            varchar(20)   NULL,              -- 檢驗員離場
    GroupId              varchar(3)    NOT NULL,          -- 經辦組別（框線一課長/經理 + 框線二經辦組）
    ImproveDeadline      date          NULL,              -- 限期改善日期
    NeedImprove          bit           NOT NULL CONSTRAINT DF_Pr4Form_NeedImprove DEFAULT 0,  -- 紀錄簽發：是否需進行改善

    IsJump               bit           NOT NULL CONSTRAINT DF_Pr4Form_Jump DEFAULT 0,

    InspectionChiefId    varchar(6)    NULL,              -- 框線一 課長
    InspectionManagerId  varchar(6)    NULL,              -- 框線一 經理
    HandlingOfficerId    varchar(6)    NULL,              -- 框線二 經辦
    HandlingChiefId      varchar(6)    NULL,              -- 框線二 課長
    HandlingManagerId    varchar(6)    NULL               -- 框線二 經理
);

CREATE TABLE dbo.Pr4Item (
    ItemId             bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Pr4Item PRIMARY KEY,
    FormId             bigint         NOT NULL CONSTRAINT FK_Pr4Item_Form REFERENCES dbo.Pr4Form(FormId),
    Kind               tinyint        NOT NULL,            -- 0 優點 / 1 缺失 / 2 建議改善
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
    PenaltyTicketNo    nvarchar(30)   NULL                 -- 逾期/罰款時登記（Pr5 連動留後續）
);
CREATE INDEX IX_Pr4Item_Form ON dbo.Pr4Item(FormId, Kind, Ordinal);

-- Pr4 功能權限（新增編號 230-239）。
INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES (230, NULL, N'承攬商工作場所工安抽查紀錄表', NULL, 230);
INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES
 (231, 230, N'開單',          '/Pr4/Create', 231),
 (232, 230, N'檢驗員簽核',     NULL,          232),
 (233, 230, N'經辦組查證',     NULL,          233),
 (239, 230, N'查詢',          '/Pr4',        239);

-- 系統管理員（Role 9）補得新功能。
INSERT dbo.RoleFeature (RoleId, FeatureId)
SELECT 9, f.FeatureId FROM dbo.Feature f
WHERE f.FeatureId BETWEEN 230 AND 239
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleFeature rf WHERE rf.RoleId = 9 AND rf.FeatureId = f.FeatureId);
