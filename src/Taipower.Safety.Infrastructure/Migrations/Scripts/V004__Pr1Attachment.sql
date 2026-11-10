-- Pr1 項目照片：缺失現況（Before）與改善後（After）。檔案本體存於檔案系統
-- (Storage.RootPath)，此表只存中繼資料與相對路徑 StoredName。
CREATE TABLE dbo.Pr1Attachment (
    AttachmentId  bigint        IDENTITY(1,1) NOT NULL CONSTRAINT PK_Pr1Attachment PRIMARY KEY,
    ItemId        bigint        NOT NULL CONSTRAINT FK_Pr1Attachment_Item REFERENCES dbo.Pr1Item(ItemId),
    Category      varchar(10)   NOT NULL,          -- Before / After
    OriginalName  nvarchar(260) NOT NULL,          -- 使用者原始檔名（僅顯示/下載用）
    StoredName    nvarchar(200) NOT NULL,          -- 相對於儲存根的路徑（伺服器產生）
    ContentType   varchar(100)  NOT NULL,
    SizeBytes     bigint        NOT NULL,
    UploadedBy    varchar(6)    NULL,
    UploadedAt    datetime2(0)  NOT NULL CONSTRAINT DF_Pr1Attachment_At DEFAULT SYSDATETIME()
);
CREATE INDEX IX_Pr1Attachment_Item ON dbo.Pr1Attachment(ItemId, Category, AttachmentId);
