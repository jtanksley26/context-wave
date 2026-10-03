using System.Security.Principal;

namespace MdReader.Core;

/// <summary>
/// Decides whether a pipe was made by this user's own Context Wave. The pipe's owner is whatever
/// default owner the app's token had: the user, or Administrators for a user in that group. A hook
/// process can start with the other default, so asking for an exact match with the client's own
/// default owner (what PipeOptions.CurrentUserOnly does) refuses the user's own app.
/// </summary>
public static class PipeOwner
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static bool IsTrusted(
        SecurityIdentifier? owner, SecurityIdentifier user, SecurityIdentifier? tokenOwner, bool userIsAdministrator)
    {
        if (owner is null) return false;
        if (owner == user || owner == tokenOwner) return true;
        // Only a member of Administrators can create an object owned by the group.
        return userIsAdministrator && owner == Administrators;
    }

    /// <summary>Applies <see cref="IsTrusted"/> to the current Windows identity.</summary>
    public static bool IsTrustedForCurrentUser(SecurityIdentifier? owner)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        return IsTrusted(owner, identity.User!, identity.Owner, isAdministrator);
    }
}
