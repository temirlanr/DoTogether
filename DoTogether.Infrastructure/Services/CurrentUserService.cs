using System.Security.Claims;
using DoTogether.Application.Interfaces;
using Microsoft.AspNetCore.Http;

namespace DoTogether.Infrastructure.Services;

public class CurrentUserService(IHttpContextAccessor httpContextAccessor) : ICurrentUserService
{
    public Guid UserId
    {
        get
        {
            var sub = httpContextAccessor.HttpContext?.User.FindFirstValue("sub")
                      ?? httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return sub is not null ? Guid.Parse(sub) : Guid.Empty;
        }
    }

    public bool IsAuthenticated =>
        httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;
}
