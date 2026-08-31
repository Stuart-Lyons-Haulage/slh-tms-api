namespace Slh.Tms.Api.Authorization;

internal static class TmsRoles
{
    public const string Viewer = "TMS.Viewer";
    public const string Planner = "TMS.Planner";
    public const string Dispatcher = "TMS.Dispatcher";
    public const string OperationsController = "TMS.OperationsController";
    public const string Approver = "TMS.Approver";
    public const string MasterDataAdmin = "TMS.MasterDataAdmin";
    public const string SystemAdmin = "TMS.SystemAdmin";

    public static readonly string[] Access =
    [
        Viewer,
        Planner,
        Dispatcher,
        OperationsController,
        Approver,
        MasterDataAdmin,
        SystemAdmin
    ];

    public static readonly string[] Write =
    [
        Planner,
        Dispatcher,
        OperationsController,
        Approver,
        MasterDataAdmin,
        SystemAdmin
    ];

    public static readonly string[] Dispatch =
    [
        Dispatcher,
        OperationsController,
        Approver,
        MasterDataAdmin,
        SystemAdmin
    ];

    public static readonly string[] Approve =
    [
        Approver,
        MasterDataAdmin,
        SystemAdmin
    ];

    public static readonly string[] MasterData =
    [
        MasterDataAdmin,
        SystemAdmin
    ];

    public static readonly string[] Admin =
    [
        SystemAdmin
    ];
}

internal static class TmsPolicies
{
    public const string Access = "TmsAccess";
    public const string Write = "TmsWrite";
    public const string Dispatch = "TmsDispatch";
    public const string Approve = "TmsApprove";
    public const string MasterData = "TmsMasterData";
    public const string Admin = "TmsAdmin";
}
