using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Enrollment;

/// <summary>
/// Transport for <c>POST /enrollment/request</c>. Kept behind an interface because the endpoint's answer shape is
/// still OPEN (skill §15 item 8).
/// </summary>
public interface IEnrollmentClient
{
    Uri Endpoint { get; }

    /// <summary>
    /// Sends one enrollment request. A definitive refusal (token invalid, expired or declined) comes back as
    /// <see cref="EnrollmentStatuses.Rejected"/>; transient failures throw so the caller retries.
    /// </summary>
    Task<EnrollmentResponse> RequestAsync(EnrollmentRequest request, CancellationToken cancellationToken);
}
