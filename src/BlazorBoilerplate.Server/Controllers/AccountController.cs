﻿using BlazorBoilerplate.Server.Data;
using BlazorBoilerplate.Server.Helpers;
using BlazorBoilerplate.Server.Middleware.Wrappers;
using BlazorBoilerplate.Server.Models;
using BlazorBoilerplate.Server.Services;
using BlazorBoilerplate.Shared;
using BlazorBoilerplate.Shared.AuthorizationDefinitions;
using BlazorBoilerplate.Shared.Dto;
using IdentityModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace BlazorBoilerplate.Server.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AccountController : ControllerBase
    {
        private static readonly UserInfoDto LoggedOutUser = new UserInfoDto { IsAuthenticated = false, Roles = new List<string>() };

        private readonly ILogger<AccountController> _logger;
        private readonly IAccountService _accountService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole<Guid>> _roleManager;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly IUserProfileService _userProfileService;
        private readonly ApplicationDbContext _db;

        public AccountController(IAccountService accountService,
            UserManager<ApplicationUser> userManager,
            ApplicationDbContext db,
            SignInManager<ApplicationUser> signInManager,
            ILogger<AccountController> logger,
            RoleManager<IdentityRole<Guid>> roleManager,
            IEmailService emailService,
            IUserProfileService userProfileService,
            IConfiguration configuration)
        {
            _accountService = accountService;
            _userManager = userManager;
            _signInManager = signInManager;
            _logger = logger;
            _roleManager = roleManager;
            _emailService = emailService;
            _userProfileService = userProfileService;
            _configuration = configuration;
            _db = db;
        }

        // POST: api/Account/Login
        [HttpPost("Login")]
        [AllowAnonymous]
        [ProducesResponseType(204)]
        [ProducesResponseType(401)]
        public async Task<ApiResponse> Login(LoginDto parameters)
        {
            if (!ModelState.IsValid)
            {
                return new ApiResponse(400, "User Model is Invalid");
            }

            try
            {
                var result = await _signInManager.PasswordSignInAsync(parameters.UserName, parameters.Password, parameters.RememberMe, true);

                // If lock out activated and the max. amounts of attempts is reached.
                if (result.IsLockedOut)
                {
                    _logger.LogInformation("User Locked out: {0}", parameters.UserName);
                    return new ApiResponse(401, "User is locked out!");
                }

                // If your email is not confirmed but you require it in the settings for login.
                if (result.IsNotAllowed)
                {
                    _logger.LogInformation("User not allowed to log in: {0}", parameters.UserName);
                    return new ApiResponse(401, "Login not allowed!");
                }

                if (result.Succeeded)
                {
                    _logger.LogInformation("Logged In: {0}", parameters.UserName);
                    return new ApiResponse(200, _userProfileService.GetLastPageVisited(parameters.UserName));
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Login Failed: " + ex.Message);
            }

            _logger.LogInformation("Invalid Password for user {0}}", parameters.UserName);
            return new ApiResponse(401, "Login Failed");
        }

        // POST: api/Account/Register
        [HttpPost("Register")]
        [AllowAnonymous]
        public async Task<ApiResponse> Register(RegisterDto parameters)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return new ApiResponse(400, "User Model is Invalid");
                }

                var requireConfirmEmail = Convert.ToBoolean(_configuration["BlazorBoilerplate:RequireConfirmedEmail"] ?? "false");

                await _accountService.RegisterNewUserAsync(parameters.UserName, parameters.Email, parameters.Password, requireConfirmEmail);

                if (requireConfirmEmail)
                {
                    return new ApiResponse(200, "Register User Success");
                }
                else
                {
                    return await Login(new LoginDto
                    {
                        UserName = parameters.UserName,
                        Password = parameters.Password
                    });
                }
            }
            catch (DomainException ex)
            {
                _logger.LogError("Register User Failed: {0}, {1}", ex.Description, ex.Message);
                return new ApiResponse(400, $"Register User Failed: {ex.Description} ");
            }
            catch (Exception ex)
            {
                _logger.LogError("Register User Failed: {0}", ex.Message);
                return new ApiResponse(400, "Register User Failed");
            }
        }

        // POST: api/Account/ConfirmEmail
        [HttpPost("ConfirmEmail")]
        [AllowAnonymous]
        public async Task<ApiResponse> ConfirmEmail(ConfirmEmailDto parameters)
        {
            if (!ModelState.IsValid)
            {
                return new ApiResponse(400, "User Model is Invalid");
            }

            if (parameters.UserId == null || parameters.Token == null)
            {
                return new ApiResponse(404, "User does not exist");
            }

            var user = await _userManager.FindByIdAsync(parameters.UserId);
            if (user == null)
            {
                _logger.LogInformation("User does not exist: {0}", parameters.UserId);
                return new ApiResponse(404, "User does not exist");
            }

            string token = parameters.Token;
            var result = await _userManager.ConfirmEmailAsync(user, token);
            if (!result.Succeeded)
            {
                _logger.LogInformation("User Email Confirmation Failed: {0}", result.Errors.FirstOrDefault()?.Description);
                return new ApiResponse(400, "User Email Confirmation Failed");
            }

            await _signInManager.SignInAsync(user, true);

            return new ApiResponse(200, "Success");
        }

        // POST: api/Account/ForgotPassword
        [HttpPost("ForgotPassword")]
        [AllowAnonymous]
        public async Task<ApiResponse> ForgotPassword(ForgotPasswordDto parameters)
        {
            if (!ModelState.IsValid)
            {
                return new ApiResponse(400, "User Model is Invalid");
            }

            var user = await _userManager.FindByEmailAsync(parameters.Email);
            if (user == null || !(await _userManager.IsEmailConfirmedAsync(user)))
            {
                _logger.LogInformation("Forgot Password with non-existent email / user: {0}", parameters.Email);
                // Don't reveal that the user does not exist or is not confirmed
                return new ApiResponse(200, "Success");
            }

            #region Forgot Password Email

            try
            {
                // For more information on how to enable account confirmation and password reset please visit http://go.microsoft.com/fwlink/?LinkID=532713
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                string callbackUrl = string.Format("{0}/Account/ResetPassword/{1}?token={2}", _configuration["BlazorBoilerplate:ApplicationUrl"], user.Id, token); //token must be a query string parameter as it is very long

                var email = new EmailMessageDto();
                email.ToAddresses.Add(new EmailAddressDto(user.Email, user.Email));
                email.BuildForgotPasswordEmail(user.UserName, callbackUrl, token); //Replace First UserName with Name if you want to add name to Registration Form

                _logger.LogInformation("Forgot Password Email Sent: {0}", user.Email);
                await _emailService.SendEmailAsync(email);
                return new ApiResponse(200, "Forgot Password Email Sent");
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Forgot Password email failed: {0}", ex.Message);
            }

            #endregion Forgot Password Email

            return new ApiResponse(200, "Success");
        }

        // PUT: api/Account/ResetPassword
        [HttpPost("ResetPassword")]
        [AllowAnonymous]
        public async Task<ApiResponse> ResetPassword(ResetPasswordDto parameters)
        {
            if (!ModelState.IsValid)
            {
                return new ApiResponse(400, "User Model is Invalid");
            }

            var user = await _userManager.FindByIdAsync(parameters.UserId);
            if (user == null)
            {
                _logger.LogInformation("User does not exist: {0}", parameters.UserId);
                return new ApiResponse(404, "User does not exist");
            }

            #region Reset Password Successful Email

            try
            {
                IdentityResult result = await _userManager.ResetPasswordAsync(user, parameters.Token, parameters.Password);
                if (result.Succeeded)
                {
                    #region Email Successful Password change

                    var email = new EmailMessageDto();
                    email.ToAddresses.Add(new EmailAddressDto(user.Email, user.Email));
                    email.BuildPasswordResetEmail(user.UserName); //Replace First UserName with Name if you want to add name to Registration Form

                    _logger.LogInformation("Reset Password Successful Email Sent: {0}", user.Email);
                    await _emailService.SendEmailAsync(email);

                    #endregion Email Successful Password change

                    return new ApiResponse(200, String.Format("Reset Password Successful Email Sent: {0}", user.Email));
                }
                else
                {
                    _logger.LogInformation("Error while resetting the password!: {0}", user.UserName);
                    return new ApiResponse(400, string.Format("Error while resetting the password!: {0}", user.UserName));
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Reset Password failed: {0}", ex.Message);
                return new ApiResponse(400, string.Format("Error while resetting the password!: {0}", ex.Message));
            }

            #endregion Reset Password Successful Email
        }

        // POST: api/Account/Logout
        [HttpPost("Logout")]
        [Authorize]
        public async Task<ApiResponse> Logout()
        {
            await _signInManager.SignOutAsync();
            return new ApiResponse(200, "Logout Successful");
        }

        [HttpGet("UserInfo")]
        [ProducesResponseType(200)]
        [ProducesResponseType(401)]
        public async Task<ApiResponse> UserInfo()
        {
            UserInfoDto userInfo = await BuildUserInfo();
            return new ApiResponse(200, "Retrieved UserInfo", userInfo); ;
        }

        private async Task<UserInfoDto> BuildUserInfo()
        {
            var user = await _userManager.GetUserAsync(User);

            if (user != null)
            {
                try
                {
                    return new UserInfoDto
                    {
                        IsAuthenticated = User.Identity.IsAuthenticated,
                        UserName = user.UserName,
                        Email = user.Email,
                        FirstName = user.FirstName,
                        LastName = user.LastName,
                        UserId = user.Id,
                        //Optionally: filter the claims you want to expose to the client
                        ExposedClaims = User.Claims.Select(c => new KeyValuePair<string, string>(c.Type, c.Value)).ToList(),
                        Roles = ((ClaimsIdentity)User.Identity).Claims
                                .Where(c => c.Type == "role")
                                .Select(c => c.Value).ToList()
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not build UserInfoDto: " + ex.Message);
                }
            }
            else
            {
                return new UserInfoDto();
            }

            return null;
        }

        // DELETE: api/Account/5
        [HttpPost("UpdateUser")]
        [Authorize]
        public async Task<ApiResponse> UpdateUser(UserInfoDto userInfo)
        {
            if (!ModelState.IsValid)
            {
                return new ApiResponse(400, "User Model is Invalid");
            }

            var user = await _userManager.FindByEmailAsync(userInfo.Email);

            if (user == null)
            {
                _logger.LogInformation("User does not exist: {0}", userInfo.Email);
                return new ApiResponse(404, "User does not exist");
            }

            user.FirstName = userInfo.FirstName;
            user.LastName = userInfo.LastName;
            user.Email = userInfo.Email;

            var result = await _userManager.UpdateAsync(user);

            if (!result.Succeeded)
            {
                _logger.LogInformation("User Update Failed: {0}", result.Errors.FirstOrDefault()?.Description);
                return new ApiResponse(400, "User Update Failed");
            }

            return new ApiResponse(200, "User Updated Successfully");
        }

        ///----------Admin User Management Interface Methods

        // POST: api/Account/Create
        [HttpPost("Create")]
        [Authorize(Policy = Policies.IsAdmin)]
        public async Task<ApiResponse> Create(RegisterDto parameters)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return new ApiResponse(400, "User Model is Invalid");
                }

                var user = new ApplicationUser
                {
                    UserName = parameters.UserName,
                    Email = parameters.Email
                };

                user.UserName = parameters.UserName;
                var result = await _userManager.CreateAsync(user, parameters.Password);
                if (!result.Succeeded)
                {
                    return new ApiResponse(400, "Register User Failed: " + result.Errors.FirstOrDefault()?.Description);
                }
                else
                {
                    var claimsResult = _userManager.AddClaimsAsync(user, new Claim[]{
                        new Claim(Policies.IsUser,""),
                        new Claim(JwtClaimTypes.Name, parameters.UserName),
                        new Claim(JwtClaimTypes.Email, parameters.Email),
                        new Claim(JwtClaimTypes.EmailVerified, "false", ClaimValueTypes.Boolean)
                    }).Result;
                }

                //Role - Here we tie the new user to the "User" role
                await _userManager.AddToRoleAsync(user, "User");

                if (Convert.ToBoolean(_configuration["BlazorBoilerplate:RequireConfirmedEmail"] ?? "false"))
                {
                    #region New  User Confirmation Email

                    try
                    {
                        // For more information on how to enable account confirmation and password reset please visit http://go.microsoft.com/fwlink/?LinkID=532713
                        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
                        string callbackUrl = string.Format("{0}/Account/ConfirmEmail/{1}?token={2}", _configuration["BlazorBoilerplate:ApplicationUrl"], user.Id, token);

                        var email = new EmailMessageDto();
                        email.ToAddresses.Add(new EmailAddressDto(user.Email, user.Email));
                        email = EmailTemplates.BuildNewUserConfirmationEmail(email, user.UserName, user.Email, callbackUrl, user.Id.ToString(), token); //Replace First UserName with Name if you want to add name to Registration Form

                        _logger.LogInformation("New user created: {0}", user);
                        await _emailService.SendEmailAsync(email);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation("New user email failed: {0}", ex.Message);
                    }

                    #endregion New  User Confirmation Email

                    return new ApiResponse(200, "Create User Success");
                }

                #region New  User Email

                try
                {
                    var email = new EmailMessageDto();
                    email.ToAddresses.Add(new EmailAddressDto(user.Email, user.Email));
                    email.BuildNewUserEmail(user.FullName, user.UserName, user.Email, parameters.Password);

                    _logger.LogInformation("New user created: {0}", user);
                    await _emailService.SendEmailAsync(email);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation("New user email failed: {0}", ex.Message);
                }

                #endregion New  User Email

                UserInfoDto userInfo = new UserInfoDto
                {
                    IsAuthenticated = false,
                    UserName = user.UserName,
                    Email = user.Email,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    //ExposedClaims = user.Claims.ToDictionary(c => c.Type, c => c.Value),
                    Roles = new List<string> { "User" }
                };

                return new ApiResponse(200, "Created New User", userInfo);
            }
            catch (Exception ex)
            {
                _logger.LogError("Create User Failed: {0}", ex.Message);
                return new ApiResponse(400, "Create User Failed");
            }
        }

        // DELETE: api/Account/5
        [HttpDelete("{id}")]
        [Authorize(Policy = Policies.IsAdmin)]
        public async Task<ApiResponse> Delete(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user.Id == null)
            {
                return new ApiResponse(404, "User does not exist");
            }
            try
            {
                //EF: not a fan this will delete old ApiLogs
                var apiLogs = _db.ApiLogs.Where(a => a.ApplicationUserId == user.Id);
                foreach (var apiLog in apiLogs)
                {
                    _db.ApiLogs.Remove(apiLog);
                }
                _db.SaveChanges();

                await _userManager.DeleteAsync(user);
                return new ApiResponse(200, "User Deletion Successful");
            }
            catch
            {
                return new ApiResponse(400, "User Deletion Failed");
            }
        }

        [HttpGet("GetUser")]
        public ApiResponse GetUser()
        {
            UserInfoDto userInfo = User != null && User.Identity.IsAuthenticated
                ? new UserInfoDto { UserName = User.Identity.Name, IsAuthenticated = true }
                : LoggedOutUser;
            return new ApiResponse(200, "Get User Successful", userInfo);
        }

        [HttpGet("ListRoles")]
        [Authorize]
        public async Task<ApiResponse> ListRoles()
        {
            var roleList = _roleManager.Roles.Select(x => x.Name).ToList();
            return new ApiResponse(200, "", roleList);
        }

        [HttpPut]
        [Authorize(Policy = Policies.IsAdmin)]
        // PUT: api/Account/5
        public async Task<ApiResponse> Update([FromBody] UserInfoDto userInfo)
        {
            if (!ModelState.IsValid)
            {
                return new ApiResponse(400, "User Model is Invalid");
            }

            // retrieve full user object for updating
            var appUser = await _userManager.FindByIdAsync(userInfo.UserId.ToString()).ConfigureAwait(true);

            //update values
            appUser.UserName = userInfo.UserName;
            appUser.FirstName = userInfo.FirstName;
            appUser.LastName = userInfo.LastName;
            appUser.Email = userInfo.Email;

            try
            {
                var result = await _userManager.UpdateAsync(appUser).ConfigureAwait(true);
            }
            catch
            {
                return new ApiResponse(500, "Error Updating User");
            }

            if (userInfo.Roles != null)
            {
                try
                {
                    List<string> rolesToAdd = new List<string>();
                    List<string> rolesToRemove = new List<string>();
                    var currentUserRoles = (List<string>)(await _userManager.GetRolesAsync(appUser).ConfigureAwait(true));
                    foreach (var newUserRole in userInfo.Roles)
                    {
                        if (!currentUserRoles.Contains(newUserRole))
                        {
                            rolesToAdd.Add(newUserRole);
                        }
                    }
                    await _userManager.AddToRolesAsync(appUser, rolesToAdd).ConfigureAwait(true);
                    //HACK to switch to claims auth
                    foreach (var role in rolesToAdd)
                    {
                        await _userManager.AddClaimAsync(appUser, new Claim($"Is{role}", "true")).ConfigureAwait(true);
                    }

                    foreach (var role in currentUserRoles)
                    {
                        if (!userInfo.Roles.Contains(role))
                        {
                            rolesToRemove.Add(role);
                        }
                    }
                    await _userManager.RemoveFromRolesAsync(appUser, rolesToRemove).ConfigureAwait(true);

                    //HACK to switch to claims auth
                    foreach (var role in rolesToRemove)
                    {
                        await _userManager.RemoveClaimAsync(appUser, new Claim($"Is{role}", "true")).ConfigureAwait(true);
                    }
                }
                catch
                {
                    return new ApiResponse(500, "Error Updating Roles");
                }
            }
            return new ApiResponse(200, "User Updated");
        }

        [HttpPost("AdminUserPasswordReset/{id}")]
        [Authorize(Policy = Policies.IsAdmin)]
        [ProducesResponseType(204)]
        public async Task<ApiResponse> AdminResetUserPasswordAsync(Guid id, [FromBody] string newPassword)
        {
            ApplicationUser user;

            if (!ModelState.IsValid)
            {
                return new ApiResponse(400, "Model is Invalid");
            }

            try
            {
                user = await _userManager.FindByIdAsync(id.ToString());
                if (user.Id == null)
                {
                    throw new KeyNotFoundException();
                }
            }
            catch (KeyNotFoundException ex)
            {
                return new ApiResponse(400, "Unable to find user" + ex.Message);
            }
            try
            {
                var passToken = await _userManager.GeneratePasswordResetTokenAsync(user);
                var result = await _userManager.ResetPasswordAsync(user, passToken, newPassword);
                if (result.Succeeded)
                {
                    _logger.LogInformation(user.UserName + "'s password reset; Requested from Admin interface by:" + User.Identity.Name);
                    return new ApiResponse(204, user.UserName + " password reset");
                }
                else
                {
                    _logger.LogInformation(user.UserName + "'s password reset failed; Requested from Admin interface by:" + User.Identity.Name);

                    // this is going to an authenticated Admin so it should be safe/useful to send back raw error messages
                    if (result.Errors.Any())
                    {
                        string resultErrorsString = "";
                        foreach (var identityError in result.Errors)
                        {
                            resultErrorsString += identityError.Description + ", ";
                        }
                        resultErrorsString.TrimEnd(',');
                        return new ApiResponse(400, resultErrorsString);
                    }
                    else
                    {
                        throw new Exception();
                    }
                }
            }
            catch (Exception ex) // not sure if failed password reset result will throw an exception
            {
                _logger.LogInformation(user.UserName + "'s password reset failed; Requested from Admin interface by:" + User.Identity.Name);
                return new ApiResponse(400, ex.Message);
            }
        }
    }
}