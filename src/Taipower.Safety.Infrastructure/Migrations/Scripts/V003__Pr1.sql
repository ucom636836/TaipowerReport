-- Pr1 (工安巡查紀錄表) detail. Workflow state lives in dbo.FormInstance (FormType='PR1');
-- history in dbo.WorkflowHistory; this table holds the form header and per-stage flags.

CREATE TABLE dbo.Pr1Form (
    FormId                bigint        NOT NULL CONSTRAINT PK_Pr1Form PRIMARY KEY
                                        CONSTRAINT FK_Pr1Form_Instance REFERENCES dbo.FormInstance(FormId),
    PatrolType            varchar(2)    NOT NULL,          -- SI / SM / SL
    PatrolDate            date          NOT NULL,
    ProjectId             int           NOT NULL CONSTRAINT FK_Pr1Form_Project REFERENCES dbo.EngineeringProject(ProjectId),
    ContractorId          int           NOT NULL CONSTRAINT FK_Pr1Form_Contractor REFERENCES dbo.Contractor(ContractorId),
    LeaderId              varchar(6)    NOT NULL,          -- 巡查領隊
    TeamMembers           nvarchar(200) NULL,
    ImproveGroupId        varchar(3)    NULL,              -- 建單建議督促組別

    InformGroupId         varchar(3)    NULL,              -- C3 決定督促組別
    ImproveDeadline       date          NULL,
    ImproveCompletedDate  date          NULL,
    IsJump                bit           NOT NULL CONSTRAINT DF_Pr1Form_Jump DEFAULT 0,
    IsProxy               bit           NOT NULL CONSTRAINT DF_Pr1Form_Proxy DEFAULT 0,

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

CREATE TABLE dbo.Pr1Item (
    ItemId             bigint         IDENTITY(1,1) NOT NULL CONSTRAINT PK_Pr1Item PRIMARY KEY,
    FormId             bigint         NOT NULL CONSTRAINT FK_Pr1Item_Form REFERENCES dbo.Pr1Form(FormId),
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
CREATE INDEX IX_Pr1Item_Form ON dbo.Pr1Item(FormId, Kind, Ordinal);

-- Stage comment strings (replaces Pr1_C?_OpinionStr); one row per actor per stage visit.
CREATE TABLE dbo.Pr1StageComment (
    CommentId   bigint        IDENTITY(1,1) NOT NULL CONSTRAINT PK_Pr1StageComment PRIMARY KEY,
    FormId      bigint        NOT NULL CONSTRAINT FK_Pr1StageComment_Form REFERENCES dbo.Pr1Form(FormId),
    Stage       int           NOT NULL,
    ActorId     varchar(6)    NULL,
    ActorTitle  nvarchar(50)  NULL,
    Comment     nvarchar(1000) NULL,
    OccurredAt  datetime2(0)  NOT NULL CONSTRAINT DF_Pr1StageComment_At DEFAULT SYSDATETIME()
);
CREATE INDEX IX_Pr1StageComment_Form ON dbo.Pr1StageComment(FormId, Stage, CommentId);

-- Legacy forms imported for reference are flagged read-only so the new engine never routes them.
ALTER TABLE dbo.FormInstance ADD IsLegacy bit NOT NULL CONSTRAINT DF_FormInstance_Legacy DEFAULT 0;
