using Taipower.Safety.Core.Forms.Workflow;

namespace Taipower.Safety.Core.Forms.Pr4;

// Pr4 = 南部施工處承攬商工作場所工安抽查紀錄表（契約附件1-4）。
// legacy 完全未實作（無任何程式/資料表），本實作依「需求報告書」規格從零設計；設計推斷處標注。
// 流程（規格 3 本表單流程說明）：
//   檢驗員起單填表頭 + 框線一（紀錄簽發「是否需進行改善」）
//     → 陳核(檢驗員組) 課長 → 經理
//       A.需改善 → 經辦組登記桌 → 經辦員（框線二查證/罰款）→ 課長 → 經理 → 結案
//       B.免改善 → 直接結案
//   每階段皆可退回上一階。
// 推斷：框線一的課長/經理與框線二的經辦組，皆以單一「經辦組別 GroupId」解析（規格未明確區分）。
// 重用共用 Position/Transit/FormStatus 與底盤；關卡圖與步碼為 Pr4 專屬。

/// <summary>Pr4 的簽核階段。</summary>
public enum Pr4Stage
{
    Inspection = 1,  // 框線一：檢驗員 → 課長 → 經理
    Handling = 2,    // 框線二：經辦組 登記桌 → 經辦 → 課長 → 經理
    Closed = 9,
}

/// <summary>Pr4 一張單在流程中的位置，序列化進 FormInstance.CurrentStep。</summary>
public readonly record struct Pr4Step(Pr4Stage Stage, Position Position, Transit Transit)
{
    public static readonly Pr4Step Draft = new(Pr4Stage.Inspection, Position.Creator, Transit.Working);
    public override string ToString() => $"{Stage}/{Position}/{Transit}";
}

public enum Pr4Action
{
    Submit,           // 送出 / 上呈 / 建單 / 結案(經理)
    AssignChief,      // 登記桌派課長
    AssignOfficer,    // 登記桌派經辦
    Withdraw,         // 抽回
    ReturnToOfficer,  // 退回經辦
    ReturnToChief,    // 退回課長
    ReturnToDesk,     // 退回登記桌
    ReturnToCreator,  // 退回檢驗員
}

public enum Pr4AssigneeRule
{
    None,
    GroupChief,      // 經辦組別 課長 (Rank 4)
    GroupManager,    // 經辦組別 經理 (Rank 3)
    HandlingDesk,    // 經辦組別 登記桌 (Role 3)
    SpecificPerson,  // 指派對象
    AssignedChief,   // 本區塊已簽核課長
    OfficerOfRecord, // 本區塊已簽核經辦
    Creator,         // 檢驗員 (起單人)
    Self,
}

public sealed record Pr4Transition(
    Pr4Step To,
    Pr4AssigneeRule Assignee,
    bool SendEmail,
    bool IncrementRetimes = false,
    FormStatus Status = FormStatus.InProgress,
    string? HistoryLabel = null);

public sealed record Pr4ActionContext
{
    /// <summary>紀錄簽發「是否需進行改善」(檢驗員起單時勾選)；決定經理核後走經辦組或直接結案。</summary>
    public bool NeedImprove { get; init; }
    /// <summary>跳關：經辦直送經理，略過課長。</summary>
    public bool IsJump { get; init; }
}

/// <summary>Pr4 簽核流程狀態機 (純邏輯，可單元測試)。</summary>
public static class Pr4Workflow
{
    public static IReadOnlyList<Pr4Action> AllowedActions(Pr4Step step) => step.Stage switch
    {
        Pr4Stage.Inspection => step.Position switch
        {
            Position.Creator => [Pr4Action.Submit],
            Position.Chief => [Pr4Action.Submit, Pr4Action.ReturnToCreator],
            Position.Manager => [Pr4Action.Submit, Pr4Action.ReturnToChief],
            _ => [],
        },
        Pr4Stage.Handling => step.Position switch
        {
            Position.Assign => [Pr4Action.AssignChief, Pr4Action.AssignOfficer, Pr4Action.ReturnToCreator],
            Position.Officer => [Pr4Action.Submit, Pr4Action.Withdraw, Pr4Action.ReturnToDesk],
            Position.Chief => [Pr4Action.Submit, Pr4Action.Withdraw, Pr4Action.ReturnToOfficer],
            Position.Manager => [Pr4Action.Submit, Pr4Action.Withdraw, Pr4Action.ReturnToOfficer, Pr4Action.ReturnToChief],
            _ => [],
        },
        _ => [],
    };

    public static Pr4Transition? Next(Pr4Step step, Pr4Action action, Pr4ActionContext ctx)
    {
        if (!AllowedActions(step).Contains(action)) return null;
        return step.Stage switch
        {
            Pr4Stage.Inspection => InspectionStage(step, action, ctx),
            Pr4Stage.Handling => HandlingStage(step, action, ctx),
            _ => null,
        };
    }

    private static Pr4Transition? InspectionStage(Pr4Step step, Pr4Action action, Pr4ActionContext ctx) => (step.Position, action) switch
    {
        // 檢驗員送出 → 陳核經辦組課長。
        (Position.Creator, Pr4Action.Submit) => new Pr4Transition(
            new Pr4Step(Pr4Stage.Inspection, Position.Chief, Transit.Submitted),
            Pr4AssigneeRule.GroupChief, SendEmail: true, HistoryLabel: "建單"),

        // 課長核 → 經理。
        (Position.Chief, Pr4Action.Submit) => new Pr4Transition(
            new Pr4Step(Pr4Stage.Inspection, Position.Manager, Transit.Submitted),
            Pr4AssigneeRule.GroupManager, true, HistoryLabel: "簽核"),

        (Position.Chief, Pr4Action.ReturnToCreator) => new Pr4Transition(
            Pr4Step.Draft with { Transit = Transit.Submitted },
            Pr4AssigneeRule.Creator, true, IncrementRetimes: true, HistoryLabel: "退回檢驗員"),

        // 經理核：需改善→經辦組登記桌；免改善→直接結案。
        (Position.Manager, Pr4Action.Submit) => ctx.NeedImprove
            ? new Pr4Transition(new Pr4Step(Pr4Stage.Handling, Position.Assign, Transit.Submitted),
                Pr4AssigneeRule.HandlingDesk, true, HistoryLabel: "需改善—送經辦組")
            : new Pr4Transition(new Pr4Step(Pr4Stage.Closed, Position.Manager, Transit.Submitted),
                Pr4AssigneeRule.Self, false, Status: FormStatus.Closed, HistoryLabel: "免改善結案"),

        (Position.Manager, Pr4Action.ReturnToChief) => new Pr4Transition(
            new Pr4Step(Pr4Stage.Inspection, Position.Chief, Transit.Submitted),
            Pr4AssigneeRule.AssignedChief, true, HistoryLabel: "退回課長"),

        _ => null,
    };

    private static Pr4Transition? HandlingStage(Pr4Step step, Pr4Action action, Pr4ActionContext ctx)
    {
        switch (action)
        {
            case Pr4Action.AssignChief:
                return new Pr4Transition(step with { Position = Position.Assign, Transit = Transit.Submitted },
                    Pr4AssigneeRule.SpecificPerson, SendEmail: true, HistoryLabel: "派單（課長）");

            case Pr4Action.AssignOfficer:
                return new Pr4Transition(new Pr4Step(Pr4Stage.Handling, Position.Officer, Transit.Submitted),
                    Pr4AssigneeRule.SpecificPerson, SendEmail: true, HistoryLabel: "派單（經辦）");

            case Pr4Action.Submit when step.Position == Position.Officer:
                return ctx.IsJump
                    ? new Pr4Transition(new Pr4Step(Pr4Stage.Handling, Position.Manager, Transit.Submitted), Pr4AssigneeRule.GroupManager, true, HistoryLabel: "簽核")
                    : new Pr4Transition(new Pr4Step(Pr4Stage.Handling, Position.Chief, Transit.Submitted), Pr4AssigneeRule.AssignedChief, true, HistoryLabel: "簽核");

            case Pr4Action.Submit when step.Position == Position.Chief:
                return new Pr4Transition(new Pr4Step(Pr4Stage.Handling, Position.Manager, Transit.Submitted), Pr4AssigneeRule.GroupManager, true, HistoryLabel: "簽核");

            // 經辦組經理核定 → 結案。
            case Pr4Action.Submit when step.Position == Position.Manager:
                return new Pr4Transition(new Pr4Step(Pr4Stage.Closed, Position.Manager, Transit.Submitted),
                    Pr4AssigneeRule.Self, false, Status: FormStatus.Closed, HistoryLabel: "結案");

            case Pr4Action.ReturnToOfficer:
                return new Pr4Transition(new Pr4Step(Pr4Stage.Handling, Position.Officer, Transit.Submitted),
                    Pr4AssigneeRule.OfficerOfRecord, true, HistoryLabel: "退回經辦");

            case Pr4Action.ReturnToChief:
                return new Pr4Transition(new Pr4Step(Pr4Stage.Handling, Position.Chief, Transit.Submitted),
                    Pr4AssigneeRule.AssignedChief, true, HistoryLabel: "退回課長");

            case Pr4Action.ReturnToDesk:
                return new Pr4Transition(new Pr4Step(Pr4Stage.Handling, Position.Assign, Transit.Submitted),
                    Pr4AssigneeRule.HandlingDesk, true, HistoryLabel: "退回登記桌");

            case Pr4Action.ReturnToCreator:
                return new Pr4Transition(Pr4Step.Draft with { Transit = Transit.Submitted },
                    Pr4AssigneeRule.Creator, true, IncrementRetimes: true, HistoryLabel: "退回檢驗員");

            default:
                return null;
        }
    }
}
