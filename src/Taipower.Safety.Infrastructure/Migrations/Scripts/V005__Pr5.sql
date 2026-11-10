-- Pr5 工程罰款管理（工安缺失改善通知單 / 罰單）。一次性開立，狀態單一欄位，
-- 不走 FormInstance 的多關卡簽核。可獨立開單或由 Pr1 缺失項連動（SourceItemId）。
CREATE TABLE dbo.Pr5Penalty (
    PenaltyId         bigint        IDENTITY(1,1) NOT NULL CONSTRAINT PK_Pr5Penalty PRIMARY KEY,
    PenaltyNumber     varchar(20)   NOT NULL CONSTRAINT UQ_Pr5Penalty_Number UNIQUE,
    Source            tinyint       NOT NULL,          -- 0 獨立 / 1 Pr1 連動
    SourceFormId      bigint        NULL CONSTRAINT FK_Pr5Penalty_Form REFERENCES dbo.FormInstance(FormId),
    SourceItemId      bigint        NULL CONSTRAINT FK_Pr5Penalty_Item REFERENCES dbo.Pr1Item(ItemId),
    ProjectId         int           NOT NULL CONSTRAINT FK_Pr5Penalty_Project REFERENCES dbo.EngineeringProject(ProjectId),
    ContractorId      int           NOT NULL CONSTRAINT FK_Pr5Penalty_Contractor REFERENCES dbo.Contractor(ContractorId),
    AuditGroupId      varchar(3)    NULL,
    HazardId          int           NULL,
    DeficiencyTypeId  int           NULL,
    DeficiencyItemId  int           NULL,
    ViolationDate     date          NULL,
    Place             nvarchar(100) NULL,
    UnitPeriod        nvarchar(50)  NULL,
    PenaltyItemCode   nvarchar(20)  NULL,
    PenaltyAmount     decimal(12,0) NULL,
    ImproveDeadline   date          NULL,
    Remark            nvarchar(300) NULL,
    Status            tinyint       NOT NULL CONSTRAINT DF_Pr5Penalty_Status DEFAULT 0,  -- 0 開立 / 1 簽收 / 2 作廢
    CreatedBy         varchar(6)    NOT NULL,
    CreatedAt         datetime2(0)  NOT NULL CONSTRAINT DF_Pr5Penalty_At DEFAULT SYSDATETIME(),
    NotifiedAt        datetime2(0)  NULL,
    RowVer            rowversion    NOT NULL
);
CREATE INDEX IX_Pr5Penalty_Source ON dbo.Pr5Penalty(SourceFormId, SourceItemId);
CREATE INDEX IX_Pr5Penalty_Project ON dbo.Pr5Penalty(ProjectId, ContractorId, Status);

-- Pr5 功能權限（新增；legacy 的罰款系統 func id 不明，故不由匯入帶入，由管理者指派）。
INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES (200, NULL, N'工程罰款管理', NULL, 200);
INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES
 (201, 200, N'開立罰單', '/Pr5/Create', 201),
 (299, 200, N'查詢',     '/Pr5',        299);

-- 系統管理員（Role 9）補得新功能。
INSERT dbo.RoleFeature (RoleId, FeatureId)
SELECT 9, f.FeatureId FROM dbo.Feature f
WHERE f.FeatureId IN (200, 201, 299)
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleFeature rf WHERE rf.RoleId = 9 AND rf.FeatureId = f.FeatureId);
