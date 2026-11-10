using Taipower.Safety.Core.Forms.Workflow;

namespace Taipower.Safety.Web.Models;

/// <summary>簽核流程的顯示字串（階段 / 位置 / 步驟 / 動作）。與表單無關，Pr1/Pr2 共用。</summary>
public static class WorkflowDisplay
{
    public static string StageName(Stage s) => s switch
    {
        Stage.Create => "開單",
        Stage.SafetyReview => "工安組簽註意見",
        Stage.Improvement => "經辦組處理改善",
        Stage.Verification => "工安組查證結果",
        Stage.Approval => "長官覆核批示",
        _ => s.ToString(),
    };

    public static string PositionName(Position p) => p switch
    {
        Position.Creator => "開單人",
        Position.Assign => "登記桌/派單",
        Position.Officer => "經辦",
        Position.Chief => "課長",
        Position.Manager => "經理",
        Position.Leader => "巡查領隊",
        Position.DeputyEng => "工程副處長",
        Position.DeputySafety => "工安副處長",
        Position.Director => "處長",
        _ => p.ToString(),
    };

    public static string StepName(WorkflowStep s, FormStatus status) => status switch
    {
        FormStatus.Closed => "已結案",
        FormStatus.Voided => "已作廢",
        _ => $"{StageName(s.Stage)}（{PositionName(s.Position)}{(s.Transit == Transit.Submitted ? "·待處理" : "·處理中")}）",
    };

    public static string ActionName(WorkflowAction a) => a switch
    {
        WorkflowAction.Submit => "送出 / 上呈",
        WorkflowAction.AssignChief => "派單（課長）",
        WorkflowAction.AssignOfficer => "派單（經辦）",
        WorkflowAction.Withdraw => "抽回",
        WorkflowAction.ReturnToOfficer => "退回經辦",
        WorkflowAction.ReturnToChief => "退回課長",
        WorkflowAction.ReturnToDesk => "退回登記桌",
        WorkflowAction.ReturnToCreator => "退回開單人",
        WorkflowAction.NotifyContractor => "通知承攬商",
        WorkflowAction.CloseDecision => "是否結案",
        WorkflowAction.Approve => "核可結案",
        WorkflowAction.Reject => "不核可作廢",
        WorkflowAction.ReturnToLeader => "退回巡查領隊",
        _ => a.ToString(),
    };
}
