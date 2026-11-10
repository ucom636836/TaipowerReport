-- Lookup rows whose ids the application code depends on.
-- Values mirror the legacy ORG_Rank / ORG_Role / Features tables so imported data maps 1:1.

INSERT dbo.OrgRank (RankId, Name, Level) VALUES
 (1, N'處長', 1), (2, N'副處長', 2), (3, N'經理', 3), (4, N'課長', 4), (5, N'經辦', 5);

INSERT dbo.OrgRole (RoleId, Name) VALUES
 (1, N'主管'), (2, N'副主管'), (3, N'登記桌'), (4, N'承辦人A'), (5, N'承辦人B'),
 (6, N'巡查領隊'), (8, N'工安管理員'), (9, N'系統管理員');

INSERT dbo.OrgGroup (GroupId, Name, ParentGroupId, SortOrder) VALUES (N'00', N'處長室', NULL, 0);
INSERT dbo.OrgGroup (GroupId, Name, ParentGroupId, SortOrder) VALUES
 (N'001', N'副處長室(一)', N'00', 1), (N'002', N'副處長室(二)', N'00', 2), (N'003', N'副處長室(三)', N'00', 3);
INSERT dbo.OrgGroup (GroupId, Name, ParentGroupId, SortOrder) VALUES
 (N'01', N'土木組', N'001', 10), (N'02', N'建築組', N'001', 11), (N'03', N'鍋爐組', N'002', 12),
 (N'10', N'儀控組', N'003', 13), (N'11', N'電務組', N'003', 14), (N'14', N'環保設備組', N'002', 15),
 (N'16', N'汽機組', N'002', 16), (N'18', N'品質組', N'001', 17), (N'20', N'工程管理組', N'002', 18),
 (N'60', N'供應組', N'001', 19), (N'61', N'會計組', N'003', 20), (N'63', N'政風組', N'002', 21),
 (N'67', N'公共關係組', N'001', 22), (N'68', N'人力資源組', N'003', 23), (N'69', N'工業安全衛生組', N'003', 24);

INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES (100, NULL, N'工安巡查紀錄表', NULL, 100);
INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES
 (101, 100, N'開單',                   '/Pr1/Create',                 101),
 (102, 100, N'工安組簽註意見-派單',    '/Pr1/Inbox?stage=SafetyReview', 102),
 (103, 100, N'工安組簽註意見',         '/Pr1/Inbox?stage=SafetyReview', 103),
 (104, 100, N'經辦組改善或處理-派單',  '/Pr1/Inbox?stage=Improvement',  104),
 (105, 100, N'經辦組改善或處理',       '/Pr1/Inbox?stage=Improvement',  105),
 (106, 100, N'工安組查證結果-派單',    '/Pr1/Inbox?stage=Verification', 106),
 (107, 100, N'工安組查證結果',         '/Pr1/Inbox?stage=Verification', 107),
 (108, 100, N'長官覆核批示',           '/Pr1/Inbox?stage=Approval',     108),
 (199, 100, N'查詢',                   '/Pr1',                        199);

INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES (600, NULL, N'系統管理', NULL, 600);
INSERT dbo.Feature (FeatureId, ParentId, Name, Path, SortOrder) VALUES
 (601, 600, N'組織維護',       '/Admin/Org',         601),
 (602, 600, N'職務角色維護',   '/Admin/RankRole',    602),
 (603, 600, N'人員資料維護',   '/Admin/Employees',   603),
 (604, 600, N'人員權限設定',   '/Admin/Permissions', 604),
 (605, 600, N'基本資料維護',   '/Admin/Reference',   605),
 (606, 600, N'操作紀錄查詢',   '/Admin/AuditLog',    606);

-- System administrators get every feature by default.
INSERT dbo.RoleFeature (RoleId, FeatureId) SELECT 9, FeatureId FROM dbo.Feature;
