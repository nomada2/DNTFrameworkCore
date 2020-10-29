using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using DNTFrameworkCore.Cryptography;
using DNTFrameworkCore.Dependency;
using DNTFrameworkCore.EFCore.Context;
using DNTFrameworkCore.Functional;
using DNTFrameworkCore.Runtime;
using DNTFrameworkCore.TestWebApp.Domain.Identity;
using DNTFrameworkCore.TestWebApp.Resources;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DNTFrameworkCore.TestWebApp.Authentication
{
    public interface IAuthenticationService : IScopedDependency
    {
        Task<SignInResult> SignInAsync(string userName, string password, bool persistent = false);
        Task SignOutAsync();
    }

    public class AuthenticationService : IAuthenticationService
    {
        private readonly IMessageLocalizer _localizer;
        private readonly IHttpContextAccessor _httpContext;
        private readonly IConfiguration _configuration;
        private readonly IUserSession _session;
        private readonly ILogger<AuthenticationService> _logger;
        private readonly IUserPasswordHashAlgorithm _password;
        private readonly DbSet<User> _users;
        private readonly DbSet<Role> _roles;
        private readonly IUnitOfWork _uow;

        public AuthenticationService(
            IMessageLocalizer localizer,
            IHttpContextAccessor httpContext,
            IUserSession session,
            ILogger<AuthenticationService> logger,
            IConfiguration configuration,
            IUnitOfWork uow,
            IUserPasswordHashAlgorithm password)
        {
            _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
            _uow = uow ?? throw new ArgumentNullException(nameof(uow));
            _httpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _password = password ?? throw new ArgumentNullException(nameof(password));

            _roles = _uow.Set<Role>();
            _users = _uow.Set<User>();
        }

        public async Task<SignInResult> SignInAsync(string userName, string password, bool persistent)
        {
            var userMaybe = await FindUserByNameAsync(userName);
            if (!userMaybe.HasValue) return SignInResult.Fail(_localizer["SignIn.Messages.Failure"]);

            var user = userMaybe.Value;

            if (_password.VerifyHashedPassword(user.PasswordHash, password) == PasswordVerificationResult.Failed)
                return SignInResult.Fail(_localizer["SignIn.Messages.Failure"]);

            if (!user.IsActive) return SignInResult.Fail(_localizer["SignIn.Messages.IsNotActive"]);

            var userId = user.Id;

            var claims = await BuildClaimsAsync(userId);

            var loginCookieExpirationDays = _configuration.GetValue("LoginCookieExpirationDays", defaultValue: 30);

            await _httpContext.HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                claims,
                new AuthenticationProperties
                {
                    IsPersistent = persistent, // "Remember Me"
                    IssuedUtc = DateTimeOffset.UtcNow,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(loginCookieExpirationDays)
                });

            _logger.LogInformation($"{userName} logged in.");

            return SignInResult.Ok();
        }

        public async Task SignOutAsync()
        {
            _logger.LogInformation($"{_session.UserName} logged out.");

            await _httpContext.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        private async Task<ClaimsPrincipal> BuildClaimsAsync(long userId)
        {
            var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);


            var maybe = await FindUserIncludeClaimsAsync(userId);
            if (!maybe.HasValue) throw new InvalidOperationException("user not found!");

            var user = maybe.Value;

            identity.AddClaim(new Claim(UserClaimTypes.UserId, user.Id.ToString(), ClaimValueTypes.Integer64));
            identity.AddClaim(new Claim(UserClaimTypes.UserName, user.UserName, ClaimValueTypes.String));
            identity.AddClaim(new Claim(UserClaimTypes.DisplayName, user.DisplayName, ClaimValueTypes.String));
            identity.AddClaim(new Claim(UserClaimTypes.SecurityStamp, user.SecurityToken, ClaimValueTypes.String));

            foreach (var claim in user.Claims)
            {
                identity.AddClaim(new Claim(claim.ClaimType, claim.ClaimValue, ClaimValueTypes.String));
            }

            var roles = await FindUserRolesIncludeClaimsAsync(user.Id);
            foreach (var role in roles)
            {
                identity.AddClaim(new Claim(UserClaimTypes.Role, role.Name, ClaimValueTypes.String));
            }

            var roleClaims = roles.SelectMany(a => a.Claims);
            foreach (var claim in roleClaims)
            {
                identity.AddClaim(new Claim(claim.ClaimType, claim.ClaimValue, ClaimValueTypes.String));
            }

            var rolePermissions = roles.SelectMany(a => a.Permissions).Select(a => a.Name);
            var grantedPermissions = user.Permissions.Where(p => p.IsGranted).Select(a => a.Name);
            var deniedPermissions = user.Permissions.Where(p => !p.IsGranted).Select(a => a.Name);

            var permissions = rolePermissions.Union(grantedPermissions).Except(deniedPermissions);
            foreach (var permission in permissions)
            {
                identity.AddClaim(new Claim(UserClaimTypes.Permission, permission, ClaimValueTypes.String));
            }
            //Todo: Set TenantId claim in MultiTenancy scenarios     
            // claims.Add(new Claim(UserClaimTypes.TenantId, user.TenantId.ToString(), ClaimValueTypes.Integer64,
            // _options.Value.Issuer));

            //Todo: Set BranchId claim in MultiBranch scenarios     
            // claims.Add(new Claim(UserClaimTypes.BranchId, user.BranchId.ToString(), ClaimValueTypes.Integer64,
            // _options.Value.Issuer));

            return new ClaimsPrincipal(identity);
        }

        private async Task<Maybe<User>> FindUserIncludeClaimsAsync(long userId)
        {
            return await _users.Include(u => u.Permissions)
                .AsNoTracking()
                .Include(u => u.Claims)
                .FirstOrDefaultAsync(x => x.Id == userId);
        }

        private async Task<IList<Role>> FindUserRolesIncludeClaimsAsync(long userId)
        {
            var query = from role in _roles
                from userRoles in role.Users
                where userRoles.UserId == userId
                select role;

            return await query
                .AsNoTracking()
                .Include(r => r.Permissions)
                .Include(r => r.Claims)
                .OrderBy(x => x.Name)
                .ToListAsync();
        }

        private async Task<Maybe<User>> FindUserByNameAsync(string userName)
        {
            var normalizedUserName = userName.ToUpperInvariant();

            return await _users.AsNoTracking()
                .FirstOrDefaultAsync(x => x.NormalizedUserName == normalizedUserName);
        }
    }
}