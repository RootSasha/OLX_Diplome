﻿using AutoMapper;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NETCore.MailKit.Core;
using Newtonsoft.Json;
using Olx.BLL.DTOs;
using Olx.BLL.Entities;
using Olx.BLL.Exceptions;
using Olx.BLL.Exstensions;
using Olx.BLL.Helpers;
using Olx.BLL.Helpers.Email;
using Olx.BLL.Interfaces;
using Olx.BLL.Models;
using Olx.BLL.Models.Authentication;
using Olx.BLL.Models.User;
using Olx.BLL.Resources;
using Olx.BLL.Specifications;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;


namespace Olx.BLL.Services
{
    public class AccountService(
        UserManager<OlxUser> userManager,
        IHttpContextAccessor  httpContext,
        IJwtService jwtService,
        IRepository<RefreshToken> tokenRepository,
        IRepository<OlxUser> userRepository,
        IRepository<Advert> advertRepository,
        IEmailService emailService,
        IConfiguration configuration,
        IMapper mapper,
        IImageService imageService,
        IValidator<ResetPasswordModel> resetPasswordModelValidator,
        IValidator<EmailConfirmationModel> emailConfirmationModelValidator,
        IValidator<UserBlockModel> userBlockModelValidator,
        IValidator<UserCreationModel> userCreationModelValidator,
        IValidator<UserEditModel> userEditModelValidator) : IAccountService
    {
        private async Task<string> CreateRefreshToken(int userId)
        {
            var refeshToken = jwtService.GetRefreshToken();
            var refreshTokenEntity = new RefreshToken
            {
                Token = refeshToken,
                OlxUserId = userId,
                ExpirationDate = DateTime.UtcNow.AddDays(jwtService.GetRefreshTokenLiveTime())
            };
            await tokenRepository.AddAsync(refreshTokenEntity);
            await tokenRepository.SaveAsync();
            return refeshToken;
        }

        private async Task<RefreshToken> CheckRefreshTokenAsync(string refreshToken)
        {
            var token = await tokenRepository.GetItemBySpec(new RefreshTokenSpecs.GetByValue(refreshToken));
            if (token is not null)
            {
                if (token.ExpirationDate > DateTime.UtcNow)
                {
                    return token;
                }
                await tokenRepository.DeleteAsync(token.Id);
                await tokenRepository.SaveAsync();
            }
            throw new HttpException(Errors.InvalidToken, HttpStatusCode.Unauthorized);
        }

        private async Task CreateUserAsync(OlxUser user,string? password = null, bool isAdmin = false)
        {
            var result = password is not null
                ? await userManager.CreateAsync(user, password)
                : await userManager.CreateAsync(user);
            if (!result.Succeeded)
            {
                throw new HttpException(Errors.UserCreateError, HttpStatusCode.InternalServerError);
            }
            await userManager.AddToRoleAsync(user, isAdmin ? Roles.Admin : Roles.User);
        }

        private async Task SendEmailConfirmationMessageAsync(OlxUser user)
        {
            var confirmationToken = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var email = EmailTemplates.GetEmailConfirmationTemplate(configuration["FrontendEmailConfirmationUrl"]!, confirmationToken, user.Email!);
            await emailService.SendAsync(user.Email, "Підтвердження електронної пошти", email, true);
        }

        private async Task CheckEmailConfirmAsync(OlxUser user)
        {
            if (!await userManager.IsEmailConfirmedAsync(user))
            {
                await SendEmailConfirmationMessageAsync(user);//??
                throw new HttpException(HttpStatusCode.Locked, new UserBlockInfo
                {
                    Message = "Ваша пошта не підтверджена. Перевірте email для підтвердження.",
                    UnlockTime = null
                });
            }
        }

        private async Task CheckLockedOutAsync(OlxUser user)
        {
            if (await userManager.IsLockedOutAsync(user))
            {
                throw new HttpException(HttpStatusCode.Locked, new UserBlockInfo
                {
                    Message = "Ваш обліковий запис заблокований.",
                    UnlockTime = user.LockoutEnd.HasValue && user.LockoutEnd.Value != DateTimeOffset.MaxValue ? user.LockoutEnd.Value.LocalDateTime : null
                });
            }
        }
        private async Task<AuthResponse> GetAuthTokens(OlxUser user)
        {
            return new()
            {
                AccessToken = jwtService.CreateToken(await jwtService.GetClaimsAsync(user)),
                RefreshToken = await CreateRefreshToken(user.Id)
            };
        }

        private async Task<OlxUser> GetCurrentUser()
        {
            var currentUserId = int.Parse(userManager.GetUserId(httpContext.HttpContext?.User!)!);
            var currentUser = await userRepository.GetItemBySpec(new OlxUserSpecs.GetById(currentUserId, UserOpt.FavoriteAdverts))
                ?? throw new HttpException(Errors.ErrorAthorizedUser, HttpStatusCode.InternalServerError);
            currentUser.LastActivity = DateTime.UtcNow;
            return currentUser;
        }

        public async Task<AuthResponse> LoginAsync(AuthRequest model)
        {
            var user = await userManager.FindByEmailAsync(model.Email);
            if (user != null) 
            {
                await CheckLockedOutAsync(user);
                await CheckEmailConfirmAsync(user);

                if (!await userManager.CheckPasswordAsync(user, model.Password))
                {
                    await userManager.AccessFailedAsync(user);
                    if (await userManager.IsLockedOutAsync(user))
                    {
                        throw new HttpException(HttpStatusCode.Locked, new UserBlockInfo
                        {
                            Message = "Ваш обліковий запис заблоковано через надто багато невірних спроб входу",
                            UnlockTime = user.LockoutEnd.HasValue ? user.LockoutEnd.Value.LocalDateTime : null
                        });
                    }
                }
                else
                {
                    await userManager.ResetAccessFailedCountAsync(user);
                    return await GetAuthTokens(user);
                }    
            }
            throw new HttpException(Errors.InvalidLoginData, HttpStatusCode.BadRequest);
        }

        public async Task<AuthResponse> GoogleLoginAsync(string googleAccessToken)
        {
            using HttpClient httpClient = new();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", googleAccessToken);
            HttpResponseMessage response = await httpClient.GetAsync(configuration["GoogleUserInfoUrl"]);
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();
            var userInfo = JsonConvert.DeserializeObject<GoogleUserInfo>(responseBody)!;
            OlxUser user = await userManager.FindByEmailAsync(userInfo.Email) ?? mapper.Map<OlxUser>(userInfo);
            if (user.Id == 0)
            {
                if (!String.IsNullOrEmpty(userInfo.Picture))
                {
                    user.Photo = await imageService.SaveImageFromUrlAsync(userInfo.Picture);
                }
                await CreateUserAsync(user);
            }
            else  await CheckLockedOutAsync(user);
            await CheckEmailConfirmAsync(user);
            return await GetAuthTokens(user);
        }

        public async Task<AuthResponse> RefreshTokensAsync(string refreshToken)
        {
            var token = await CheckRefreshTokenAsync(refreshToken);
            var user = userManager.Users.AsNoTracking().FirstOrDefault(x=>x.Id == token.OlxUserId)
                  ?? throw new HttpException(Errors.InvalidToken, HttpStatusCode.Unauthorized);
            await tokenRepository.DeleteAsync(token.Id);
            return await GetAuthTokens(user);
        }

        public async Task LogoutAsync(string refreshToken)
        {
            await userManager.UpdateUserActivityAsync(httpContext);
            var token = await tokenRepository.GetItemBySpec(new RefreshTokenSpecs.GetByValue(refreshToken));
            if (token is not null)
            {
                await tokenRepository.DeleteAsync(token.Id);
                await tokenRepository.SaveAsync();
            }
        }
        public async Task EmailConfirmAsync(EmailConfirmationModel confirmationModel)
        {
            emailConfirmationModelValidator.ValidateAndThrow(confirmationModel);
            var user = await userManager.FindByEmailAsync(confirmationModel.Email);
            if (user != null)
            {
                var result = await userManager.ConfirmEmailAsync(user, confirmationModel.Token);
                if (result.Succeeded)
                {
                    var mail = EmailTemplates.GetEmailConfirmedTemplate(configuration["FrontendLoginUrl"]!);
                    await emailService.SendAsync(user.Email, "Електронна пошта підтверджена", mail, true);
                    return;
                }
            }
            throw new HttpException(Errors.InvalidConfirmationData, HttpStatusCode.BadRequest);
        }

        public async Task FogotPasswordAsync(string email) 
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is not null) 
            {
                var passwordResetToken = await userManager.GeneratePasswordResetTokenAsync(user);
                var mail = EmailTemplates.GetPasswordResetTemplate(configuration["FrontendResetPasswordUrl"]!, passwordResetToken,user.Email!);
                await emailService.SendAsync(user.Email, "Скидання пароля", mail, true);
            }
        }

        public async Task ResetPasswordAsync(ResetPasswordModel resetPasswordModel)
        {
            resetPasswordModelValidator.ValidateAndThrow(resetPasswordModel);
            var user = await userManager.FindByEmailAsync(resetPasswordModel.Email);
            if (user is not null)
            {
                var result = await userManager.ResetPasswordAsync(user,resetPasswordModel.Token,resetPasswordModel.Password);
                if (result.Succeeded) return;
            }
            throw new HttpException(Errors.InvalidResetPasswordData, HttpStatusCode.BadRequest);
        }

        public async Task BlockUserAsync(UserBlockModel userBlockModel)
        {
            await userManager.UpdateUserActivityAsync(httpContext);
            userBlockModelValidator.ValidateAndThrow(userBlockModel);
            var user = await userManager.FindByEmailAsync(userBlockModel.Email);
            if (user is not null)
            {
                var result = userBlockModel.Block
                    ? await userManager.SetLockoutEndDateAsync(user, userBlockModel.LockoutEndDate ?? DateTimeOffset.MaxValue)
                    : await userManager.SetLockoutEndDateAsync(user, null);
                if (result.Succeeded)
                {
                    if (userBlockModel.Block)
                    {
                        string lockoutEndMessage = userBlockModel.LockoutEndDate is null
                            ? "На невизначений термін"
                            : $"Заблокований до {userBlockModel.LockoutEndDate.Value.ToLongDateString()} {userBlockModel.LockoutEndDate.Value.ToLongTimeString()}";
                        var accountBlockedTemplate = EmailTemplates.GetAccountBlockedTemplate(userBlockModel.BlockReason ?? "", lockoutEndMessage);
                        await emailService.SendAsync(user.Email, "Ваш акаунт заблоковано", accountBlockedTemplate, true);
                    }
                    else
                    {
                        var accountUnblockedTemplate = EmailTemplates.GetAccountUnblockedTemplate();
                        await emailService.SendAsync(user.Email, "Ваш акаунт розблоковано", accountUnblockedTemplate, true);
                    }
                    return;
                } 
            }
            throw new HttpException(Errors.InvalidResetPasswordData, HttpStatusCode.BadRequest);

        }

        public async Task AddUserAsync(UserCreationModel userModel, bool isAdmin = false)
        {
            if (isAdmin)
            {
                await userManager.UpdateUserActivityAsync(httpContext);
            }
            userCreationModelValidator.ValidateAndThrow(userModel);
            OlxUser user = mapper.Map<OlxUser>(userModel);
            if (userModel.ImageFile is not null)
            {
                user.Photo = await imageService.SaveImageAsync(userModel.ImageFile);
            }
            await CreateUserAsync(user, userModel.Password, isAdmin);
            if (!await userManager.IsEmailConfirmedAsync(user))
            {
                await SendEmailConfirmationMessageAsync(user);
            }
        }

        public async Task RemoveAccountAsync(string email)
        {
            await userManager.UpdateUserActivityAsync(httpContext);
            var user = await userManager.FindByEmailAsync(email) 
                ?? throw new HttpException(Errors.InvalidUserEmail, HttpStatusCode.BadRequest);
            if (await userManager.IsInRoleAsync(user, Roles.Admin))
            {
                var adminsCount =  await userManager.GetUsersInRoleAsync(Roles.Admin);
                if (adminsCount.Count <= 1)
                {
                    throw new HttpException(Errors.ActionBlocked, HttpStatusCode.Locked);
                }
            }
                     
            var result = await userManager.DeleteAsync(user);
            if (result.Succeeded)
            {
                if (user.Photo is not null)
                {
                    imageService.DeleteImageIfExists(user.Photo);
                }
            }
            else throw new HttpException(Errors.UserRemoveError, HttpStatusCode.InternalServerError);
        }

        public async Task EditUserAsync(UserEditModel userEditModel, bool isAdmin)
        {
            await userManager.UpdateUserActivityAsync(httpContext);
            var user = await userManager.FindByIdAsync(userEditModel.Id.ToString())
                ?? throw new HttpException(Errors.InvalidUserId,HttpStatusCode.NotFound);

            if (await userManager.IsInRoleAsync(user, Roles.Admin) && !isAdmin)
            {
                throw new HttpException(Errors.ActionBlocked, HttpStatusCode.Forbidden);
            }

            userEditModelValidator.ValidateAndThrow(userEditModel);
            
            var result = await userManager.ChangePasswordAsync(user, userEditModel.OldPassword!, userEditModel.Password!);
            if (!result.Succeeded)
            {
                throw new HttpException(Errors.CurrentPasswordIsNotValid, HttpStatusCode.BadRequest);
            }
            
            mapper.Map(userEditModel,user);
            if (userEditModel.ImageFile is not null)
            {
                if (user.Photo is not null)
                {
                    imageService.DeleteImageIfExists(user.Photo);
                }
                user.Photo =  await imageService.SaveImageAsync(userEditModel.ImageFile);
            }
            await userManager.UpdateAsync(user);
        }


        public async Task AddToFavoritesAsync(int advertId)
        {
            var user = await GetCurrentUser();
            var advert = await advertRepository.GetItemBySpec(new AdvertSpecs.GetById(advertId))
                ?? throw new HttpException(Errors.InvalidAdvertId, HttpStatusCode.BadRequest);

            if (user.FavoriteAdverts.All(a => a.Id != advertId))
            {
                user.FavoriteAdverts.Add(advert);
            }
            await userManager.UpdateAsync(user);
        }

        public async Task RemoveFromFavoritesAsync(int advertId)
        {
            var user = await GetCurrentUser();
            var advertToRemove = user.FavoriteAdverts.FirstOrDefault(x => x.Id == advertId)
                ?? throw new HttpException(Errors.InvalidAdvertId, HttpStatusCode.BadRequest);
            user.FavoriteAdverts.Remove(advertToRemove);
            await userManager.UpdateAsync(user);
        }

        public async Task<IEnumerable<AdvertDto>> GetFavoritesAsync()
        {
            var user = await GetCurrentUser();
            return user.FavoriteAdverts.Count > 0
                ? mapper.Map<IEnumerable<AdvertDto>>(user.FavoriteAdverts)
                : [];
        }
    }
}
