-- =============================================================
-- V001 Foundation: organisation, accounts, permissions,
-- reference data, audit log, generic workflow tables.
-- Target: SQL Server 2016+
-- =============================================================

-- ---------- Organisation ----------
CREATE TABLE dbo.OrgGroup (
    GroupId        varchar(3)    NOT NULL CONSTRAINT PK_OrgGroup PRIMARY KEY,
    Name           nvarchar(30)  NOT NULL,
    ParentGroupId  varchar(3)    NULL CONSTRAINT FK_OrgGroup_Parent REFERENCES dbo.OrgGroup(GroupId),
    SortOrder      int           NOT NULL CONSTRAINT DF_OrgGroup_Sort DEFAULT 0
);

CREATE TABLE dbo.OrgSection (
    GroupId      varchar(3)   NOT NULL CONSTRAINT FK_OrgSection_Group REFERENCES dbo.OrgGroup(GroupId),
    SectionCode  varchar(3)   NOT NULL,
    Name         nvarchar(30) NOT NULL,
    CONSTRAINT PK_OrgSection PRIMARY KEY (GroupId, SectionCode)
);

-- Rank ids are meaningful to the workflow (1 處長, 2 副處長, 3 經理, 4 課長, 5 經辦)
CREATE TABLE dbo.OrgRank (
    RankId  int          NOT NULL CONSTRAINT PK_OrgRank PRIMARY KEY,
    Name    nvarchar(20) NOT NULL,
    Level   int          NOT NULL
);

-- Role ids are meaningful to the workflow (3 登記桌, 6 巡查領隊, 9 系統管理員 ...)
CREATE TABLE dbo.OrgRole (
    RoleId  int          NOT NULL CONSTRAINT PK_OrgRole PRIMARY KEY,
    Name    nvarchar(20) NOT NULL
);

CREATE TABLE dbo.Employee (
    EmployeeId   varchar(6)    NOT NULL CONSTRAINT PK_Employee PRIMARY KEY,
    Name         nvarchar(20)  NOT NULL,
    Email        nvarchar(100) NULL,
    GroupId      varchar(3)    NULL CONSTRAINT FK_Employee_Group REFERENCES dbo.OrgGroup(GroupId),
    SectionCode  varchar(3)    NULL,
    RankId       int           NULL CONSTRAINT FK_Employee_Rank REFERENCES dbo.OrgRank(RankId),
    RoleId       int           NULL CONSTRAINT FK_Employee_Role REFERENCES dbo.OrgRole(RoleId),
    IsActive     bit           NOT NULL CONSTRAINT DF_Employee_Active DEFAULT 1,
    CreatedAt    datetime2(0)  NOT NULL CONSTRAINT DF_Employee_Created DEFAULT SYSDATETIME(),
    CreatedBy    varchar(6)    NULL,
    UpdatedAt    datetime2(0)  NULL,
    UpdatedBy    varchar(6)    NULL,
    CONSTRAINT FK_Employee_Section FOREIGN KEY (GroupId, SectionCode) REFERENCES dbo.OrgSection(GroupId, SectionCode)
);
CREATE INDEX IX_Employee_Group_Rank_Role ON dbo.Employee(GroupId, RankId, RoleId) WHERE IsActive = 1;

CREATE TABLE dbo.UserAccount (
    Account            varchar(20)   NOT NULL CONSTRAINT PK_UserAccount PRIMARY KEY,
    EmployeeId         varchar(6)    NOT NULL CONSTRAINT FK_UserAccount_Employee REFERENCES dbo.Employee(EmployeeId),
    -- "pbkdf2$<iter>$<salt b64>$<hash b64>"  or  "legacy-sha512$<salt>$<hash b64>" (rehashed on next login)
    PasswordHash       nvarchar(300) NOT NULL,
    IsActive           bit           NOT NULL CONSTRAINT DF_UserAccount_Active DEFAULT 1,
    FailedLoginCount   int           NOT NULL CONSTRAINT DF_UserAccount_Failed DEFAULT 0,
    LockoutUntil       datetime2(0)  NULL,
    PasswordChangedAt  datetime2(0)  NULL,
    LastLoginAt        datetime2(0)  NULL,
    CreatedAt          datetime2(0)  NOT NULL CONSTRAINT DF_UserAccount_Created DEFAULT SYSDATETIME(),
    CONSTRAINT UQ_UserAccount_Employee UNIQUE (EmployeeId)
);

-- ---------- Permissions ----------
CREATE TABLE dbo.Feature (
    FeatureId  int           NOT NULL CONSTRAINT PK_Feature PRIMARY KEY,
    ParentId   int           NULL CONSTRAINT FK_Feature_Parent REFERENCES dbo.Feature(FeatureId),
    Name       nvarchar(50)  NOT NULL,
    Path       varchar(200)  NULL,
    SortOrder  int           NOT NULL CONSTRAINT DF_Feature_Sort DEFAULT 0
);

CREATE TABLE dbo.RoleFeature (
    RoleId     int NOT NULL CONSTRAINT FK_RoleFeature_Role REFERENCES dbo.OrgRole(RoleId),
    FeatureId  int NOT NULL CONSTRAINT FK_RoleFeature_Feature REFERENCES dbo.Feature(FeatureId),
    CONSTRAINT PK_RoleFeature PRIMARY KEY (RoleId, FeatureId)
);

-- ---------- Reference data ----------
CREATE TABLE dbo.PenaltyVersion (
    PenaltyVersionId  int          IDENTITY(1,1) NOT NULL CONSTRAINT PK_PenaltyVersion PRIMARY KEY,
    Name              nvarchar(30) NOT NULL
);

CREATE TABLE dbo.Contractor (
    ContractorId  int           IDENTITY(1,1) NOT NULL CONSTRAINT PK_Contractor PRIMARY KEY,
    Name          nvarchar(50)  NOT NULL,
    IsActive      bit           NOT NULL CONSTRAINT DF_Contractor_Active DEFAULT 1
);

CREATE TABLE dbo.ContractorContact (
    ContactId     int           IDENTITY(1,1) NOT NULL CONSTRAINT PK_ContractorContact PRIMARY KEY,
    ContractorId  int           NOT NULL CONSTRAINT FK_ContractorContact_Contractor REFERENCES dbo.Contractor(ContractorId),
    Name          nvarchar(30)  NOT NULL,
    Email         nvarchar(100) NOT NULL
);

CREATE TABLE dbo.EngineeringProject (
    ProjectId         int            IDENTITY(1,1) NOT NULL CONSTRAINT PK_EngineeringProject PRIMARY KEY,
    ProjectNumber     varchar(20)    NOT NULL,
    Name              nvarchar(100)  NOT NULL,
    UnitPeriod        nvarchar(10)   NULL,
    PenaltyVersionId  int            NULL CONSTRAINT FK_Project_PenaltyVersion REFERENCES dbo.PenaltyVersion(PenaltyVersionId),
    IsActive          bit            NOT NULL CONSTRAINT DF_Project_Active DEFAULT 1
);

CREATE TABLE dbo.ProjectContractor (
    ProjectId     int NOT NULL CONSTRAINT FK_ProjectContractor_Project REFERENCES dbo.EngineeringProject(ProjectId),
    ContractorId  int NOT NULL CONSTRAINT FK_ProjectContractor_Contractor REFERENCES dbo.Contractor(ContractorId),
    CONSTRAINT PK_ProjectContractor PRIMARY KEY (ProjectId, ContractorId)
);

-- 危害類型 (legacy Harm)
CREATE TABLE dbo.Hazard (
    HazardId  int          IDENTITY(1,1) NOT NULL CONSTRAINT PK_Hazard PRIMARY KEY,
    Name      nvarchar(30) NOT NULL
);

-- 缺失類別 (legacy MissingType)
CREATE TABLE dbo.DeficiencyType (
    DeficiencyTypeId  int          IDENTITY(1,1) NOT NULL CONSTRAINT PK_DeficiencyType PRIMARY KEY,
    HazardId          int          NOT NULL CONSTRAINT FK_DeficiencyType_Hazard REFERENCES dbo.Hazard(HazardId),
    Name              nvarchar(50) NOT NULL
);

-- 缺失項目 (legacy MissingItems)
CREATE TABLE dbo.DeficiencyItem (
    DeficiencyItemId  int           IDENTITY(1,1) NOT NULL CONSTRAINT PK_DeficiencyItem PRIMARY KEY,
    DeficiencyTypeId  int           NOT NULL CONSTRAINT FK_DeficiencyItem_Type REFERENCES dbo.DeficiencyType(DeficiencyTypeId),
    Name              nvarchar(200) NOT NULL
);

-- ---------- Audit trail (requirement 5.1(2)) ----------
CREATE TABLE dbo.AuditLog (
    AuditLogId    bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    OccurredAt    datetime2(3)   NOT NULL CONSTRAINT DF_AuditLog_At DEFAULT SYSDATETIME(),
    EmployeeId    varchar(6)     NULL,
    EmployeeName  nvarchar(20)   NULL,
    Action        nvarchar(50)   NOT NULL,
    Target        nvarchar(100)  NULL,
    Message       nvarchar(2000) NULL,
    Success       bit            NOT NULL,
    IpAddress     varchar(45)    NULL
);
CREATE INDEX IX_AuditLog_OccurredAt ON dbo.AuditLog(OccurredAt DESC);

-- ---------- Generic workflow ----------
CREATE TABLE dbo.FormInstance (
    FormId       bigint        IDENTITY(1,1) NOT NULL CONSTRAINT PK_FormInstance PRIMARY KEY,
    FormType     varchar(10)   NOT NULL,          -- 'PR1' ...
    FormNumber   varchar(30)   NOT NULL,          -- business number, e.g. 69-1090818-01
    Revision     int           NOT NULL CONSTRAINT DF_FormInstance_Rev DEFAULT 0,   -- 重會次數
    CurrentStep  varchar(30)   NOT NULL,
    Status       varchar(20)   NOT NULL,          -- InProgress / Closed / Cancelled
    AssigneeId   varchar(6)    NULL CONSTRAINT FK_FormInstance_Assignee REFERENCES dbo.Employee(EmployeeId),
    CreatedBy    varchar(6)    NOT NULL,
    CreatedAt    datetime2(0)  NOT NULL CONSTRAINT DF_FormInstance_Created DEFAULT SYSDATETIME(),
    UpdatedAt    datetime2(0)  NULL,
    RowVer       rowversion    NOT NULL,
    CONSTRAINT UQ_FormInstance_Number UNIQUE (FormType, FormNumber)
);
CREATE INDEX IX_FormInstance_Assignee ON dbo.FormInstance(AssigneeId, FormType) WHERE Status = 'InProgress';

-- Replaces Pr1_All_OpinionStr, *_audit_id/date/opinion columns: one row per action.
CREATE TABLE dbo.WorkflowHistory (
    HistoryId    bigint          IDENTITY(1,1) NOT NULL CONSTRAINT PK_WorkflowHistory PRIMARY KEY,
    FormId       bigint          NOT NULL CONSTRAINT FK_WorkflowHistory_Form REFERENCES dbo.FormInstance(FormId),
    Revision     int             NOT NULL,
    FromStep     varchar(30)     NULL,
    ToStep       varchar(30)     NOT NULL,
    Action       varchar(30)     NOT NULL,
    ActorId      varchar(6)      NULL,
    ActorName    nvarchar(20)    NULL,
    ActorTitle   nvarchar(50)    NULL,
    AssigneeId   varchar(6)      NULL,
    Comment      nvarchar(2000)  NULL,
    OccurredAt   datetime2(0)    NOT NULL CONSTRAINT DF_WorkflowHistory_At DEFAULT SYSDATETIME()
);
CREATE INDEX IX_WorkflowHistory_Form ON dbo.WorkflowHistory(FormId, HistoryId);

CREATE TABLE dbo.Attachment (
    AttachmentId  bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Attachment PRIMARY KEY,
    FormId        bigint         NOT NULL CONSTRAINT FK_Attachment_Form REFERENCES dbo.FormInstance(FormId),
    OwnerType     varchar(20)    NOT NULL,        -- e.g. 'Pr1Item'
    OwnerId       bigint         NOT NULL,
    Category      varchar(20)    NOT NULL,        -- 'Before' / 'After'
    OriginalName  nvarchar(200)  NOT NULL,
    StoredName    varchar(100)   NOT NULL,        -- server-generated, never user supplied
    ContentType   varchar(100)   NOT NULL,
    SizeBytes     bigint         NOT NULL,
    UploadedBy    varchar(6)     NOT NULL,
    UploadedAt    datetime2(0)   NOT NULL CONSTRAINT DF_Attachment_At DEFAULT SYSDATETIME()
);
CREATE INDEX IX_Attachment_Owner ON dbo.Attachment(OwnerType, OwnerId);

-- Notification e-mails link straight to the form page; an unauthenticated
-- click goes through the normal login with ReturnUrl (no employee id in URL).
