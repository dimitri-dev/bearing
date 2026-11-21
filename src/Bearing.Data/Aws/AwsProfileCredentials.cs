using System.Diagnostics.CodeAnalysis;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.Runtime.Credentials;

namespace Bearing.Data.Aws;

/// <summary>
/// Turns an AWS profile name into the AWS SDK's <see cref="AWSCredentials"/> — the one lookup DynamoDB and
/// OpenSearch share, so the two engines cannot disagree about what "profile not found" means.
/// <para>
/// <b>What this does and does not touch.</b> Every method here returns the SDK's opaque <see cref="AWSCredentials"/>
/// object; none of them calls <c>GetCredentials</c>, so no access key, secret or token passes through this code.
/// The SDK does read the profile while answering (it opens the AWS config and credentials files, and for a
/// profile holding static keys loads them into the object it returns) — that is the SDK's reading, in this
/// process, and §1.9c words it so. Whether an engine then asks the object for its keys is that engine's decision:
/// DynamoDB never does (it hands the object to its client); OpenSearch's signer does, per request.
/// </para>
/// <para>
/// <b>Failures are typed and engine-neutral</b> (<see cref="AwsSignInProblem"/>): each engine words its own
/// sentence from the profile name and the problem, never from the SDK's text.
/// </para>
/// </summary>
internal static class AwsProfileCredentials
{
    /// <summary>
    /// The named <paramref name="profile"/>'s credentials, asked as two questions so each failure says only what
    /// was found: is the profile there (<see cref="AwsSignInProblem.ProfileNotFound"/>), and does it hold settings
    /// the SDK can build credentials from (<see cref="AwsSignInProblem.ProfileHasNoCredentials"/> — a profile that
    /// only sets a region). <see cref="CredentialProfileStoreChain.TryGetAWSCredentials"/> would answer both with
    /// one false.
    /// <para>
    /// Building the object resolves nothing over the network: an SSO or role profile is resolved by the SDK when
    /// its credentials are first asked for.
    /// </para>
    /// </summary>
    /// <param name="profile">The profile name, already trimmed and not blank.</param>
    /// <param name="profilesLocation">A credentials file to read profiles from instead of the SDK's own
    /// locations. For tests only: nothing in the app passes it.</param>
    /// <param name="credentials">The SDK's credentials object when found; null otherwise.</param>
    /// <param name="problem">Why not, when the answer is false; meaningless when it is true.</param>
    internal static bool TryNamed(
        string profile,
        string? profilesLocation,
        [NotNullWhen(true)] out AWSCredentials? credentials,
        out AwsSignInProblem problem)
    {
        var chain = profilesLocation is null
            ? new CredentialProfileStoreChain()
            : new CredentialProfileStoreChain(profilesLocation);

        if (!chain.TryGetProfile(profile, out var found))
        {
            credentials = null;
            problem = AwsSignInProblem.ProfileNotFound;
            return false;
        }

        if (!AWSCredentialsFactory.TryGetAWSCredentials(found, found.CredentialProfileStore, out credentials))
        {
            credentials = null;
            problem = AwsSignInProblem.ProfileHasNoCredentials;
            return false;
        }

        problem = AwsSignInProblem.CouldNotSignIn;
        return true;
    }

    /// <summary>
    /// The SDK's default credential chain (environment variables, the <c>default</c> profile, SSO, the container
    /// and instance metadata services, in the SDK's own order) — what a blank profile means. Resolved by the SDK,
    /// which may probe a metadata service and take a while: <paramref name="ct"/> abandons the wait, not the
    /// SDK's work. Throws what the SDK throws when the chain produces nothing; the caller words it.
    /// <para>
    /// DynamoDB does not call this: its client resolves the default chain itself on the first request.
    /// </para>
    /// </summary>
    internal static Task<AWSCredentials> DefaultChainAsync(CancellationToken ct)
        => DefaultAWSCredentialsIdentityResolver.GetCredentialsAsync().WaitAsync(ct);
}

/// <summary>What is known about a failed AWS sign-in — no more than was checked (§1.1's rule). Engine-neutral:
/// each engine maps it to its own sentence.</summary>
internal enum AwsSignInProblem
{
    /// <summary>Credentials were found and failed, or could not be produced, for a reason the SDK did not settle.</summary>
    CouldNotSignIn,

    /// <summary>No profile of that name in the AWS config or credentials file.</summary>
    ProfileNotFound,

    /// <summary>The profile exists, but the SDK found no complete set of credential settings in it (a profile that
    /// only sets <c>region</c>, an access key without its secret).</summary>
    ProfileHasNoCredentials,
}
