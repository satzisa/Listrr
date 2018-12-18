﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Listrr.API.Trakt;
using Listrr.Comparer;
using Listrr.Data;
using Listrr.Data.Trakt;
using Listrr.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Rewrite.Internal.ApacheModRewrite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Refit;
using TraktNet;
using TraktNet.Enums;
using TraktNet.Exceptions;
using TraktNet.Objects.Authentication;
using TraktNet.Objects.Get.Movies;
using TraktNet.Objects.Get.People;
using TraktNet.Objects.Get.Shows;
using TraktNet.Objects.Post.Users.CustomListItems;
using TraktNet.Requests.Parameters;
using TraktNet.Utils;

namespace Listrr.Repositories
{
    public class TraktListAPIRepository : ITraktListAPIRepository
    {

        private readonly TraktClient traktClient;
        private readonly UserManager<IdentityUser> userManager;
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly IConfiguration configuration;

        public TraktListAPIRepository(UserManager<IdentityUser> userManager, IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
        {
            this.userManager = userManager;
            this.httpContextAccessor = httpContextAccessor;
            this.configuration = configuration;

            traktClient = new TraktClient(configuration["Trakt:ClientID"], configuration["Trakt:ClientSecret"]);
        }


        public async Task<TraktList> Create(TraktList model)
        {
            await PrepareForAPIRequest();

            var response = await traktClient.Users.CreateCustomListAsync(
                "me",
                model.Name,
                Constants.LIST_Description,
                TraktAccessScope.Private,
                false,
                false
            );

            if (!response.IsSuccess) throw response.Exception;

            model.Id = response.Value.Ids.Trakt;
            model.Slug = response.Value.Ids.Slug;
            model.LastProcessed = DateTime.Now;
            model.Process = true;

            return model;
        }

        public async Task<TraktList> Get(uint id)
        {
            await PrepareForAPIRequest();

            var response = await traktClient.Users.GetCustomListAsync("me", id.ToString());

            if (!response.IsSuccess) throw response.Exception;

            return new TraktList()
            {
                Id = response.Value.Ids.Trakt,
                Slug = response.Value.Ids.Slug,
                Name = response.Value.Name
            };
        }

        public async Task<List<ITraktMovie>> MovieSearch(TraktList model)
        {
            var list = new List<ITraktMovie>();

            await MovieSearch(model, 0, 100, list);

            return list;
        }

        private async Task MovieSearch(TraktList model, uint? page, uint? limit, List<ITraktMovie> list)
        {
            var result = await traktClient.Search.GetTextQueryResultsAsync(
                TraktSearchResultType.Movie,
                model.Query,
                model.Filter_SearchField,
                new TraktSearchFilter(
                    model.Filter_Years.From,
                    model.Filter_Years.To,
                    model.Filter_Genres.Genres,
                    model.Filter_Languages.Languages,
                    model.Filter_Countries.Languages,
                    new Range<int>(
                        model.Filter_Runtimes.From,
                        model.Filter_Runtimes.To
                    ),
                    new Range<int>(
                        model.Filter_Ratings.From,
                        model.Filter_Ratings.To
                    )
                ), new TraktExtendedInfo().SetMetadata(),
                new TraktPagedParameters(page, limit)
            );


            if (!result.IsSuccess) throw result.Exception;

            foreach (var traktSearchResult in result.Value)
            {
                if(!list.Contains(traktSearchResult.Movie, new TraktMovieComparer()))
                    list.Add(traktSearchResult.Movie);
            }

            if (result.PageCount > page)
            {
                //await Task.Delay(500); //Since we dont want to destroy the trakt.tv API
                await MovieSearch(model, page + 1, limit, list);
            }
        }

        public async Task<TraktList> Update(TraktList model)
        {
            await PrepareForAPIRequest();

            await traktClient.Users.UpdateCustomListAsync(
                "me",
                model.Id.ToString(),
                model.Name,
                Constants.LIST_Description,
                TraktAccessScope.Private,
                false,
                false
            );

            return model;
        }

        
        public async Task<List<ITraktMovie>> GetMovies(TraktList model)
        {
            await PrepareForAPIRequest(model.Owner);

            var result = new List<ITraktMovie>();

            await GetMovies(model, null, null, result);

            return result;
        }

        private async Task GetMovies(TraktList model, uint? page, uint? limit, List<ITraktMovie> list)
        {
            var result = await traktClient.Users.GetCustomListItemsAsync(
                model.Owner.UserName,
                model.Slug,
                TraktListItemType.Movie,
                new TraktExtendedInfo().SetMetadata(),
                new TraktPagedParameters(
                    page,
                    limit
                )
            );

            if (!result.IsSuccess) throw result.Exception;

            foreach (var traktSearchResult in result.Value)
            {
                list.Add(traktSearchResult.Movie);
            }

            if (result.PageCount > page) await GetMovies(model, page + 1, limit, list);

        }

        public async Task AddMovies(IEnumerable<ITraktMovie> movies, TraktList list)
        {
            try
            {
                await PrepareForAPIRequest(list.Owner);

                var builder = TraktUserCustomListItemsPost.Builder();

                builder.AddMovies(movies);
                builder.AddPersons(new List<ITraktPerson>());
                builder.AddShows(new List<ITraktShow>());


                var result = await traktClient.Users.AddCustomListItemsAsync(
                    list.Owner.UserName,
                    list.Slug,
                    builder.Build(),
                    TraktListItemType.Movie
                );

                if (!result.IsSuccess)
                {
                    var ex = (TraktBadRequestException)result.Exception;



                    //result.Exception;
                }
            }
            catch (TraktBadRequestException e)
            {
                Console.WriteLine(e);
                throw;
            }
            

            //if (!result.IsSuccess)
            //{
            //    var ex = (TraktBadRequestException) result.Exception;



            //    //result.Exception;
            //}
        }


        public async Task RemoveMovies(IEnumerable<ITraktMovie> movies, TraktList list)
        {
            await PrepareForAPIRequest(list.Owner);

            var builder = new TraktUserCustomListItemsPostBuilder();
            builder.AddMovies(movies);

            var result = await traktClient.Users.RemoveCustomListItemsAsync(
                list.Owner.UserName,
                list.Slug,
                builder.Build()
            );

            if (!result.IsSuccess) throw result.Exception;
        }


        private async Task PrepareForAPIRequest(IdentityUser user = null)
        {
            if (user == null)
            {
                user = await userManager.GetUserAsync(httpContextAccessor.HttpContext.User);
            }    

            var expiresAtToken = await userManager.GetAuthenticationTokenAsync(user, Constants.TOKEN_LoginProvider, Constants.TOKEN_ExpiresAt);
            var access_token = await userManager.GetAuthenticationTokenAsync(user, Constants.TOKEN_LoginProvider, Constants.TOKEN_AccessToken);
            var refresh_token = await userManager.GetAuthenticationTokenAsync(user, Constants.TOKEN_LoginProvider, Constants.TOKEN_RefreshToken);

            var expiresAt = DateTime.Parse(expiresAtToken);

            if (expiresAt < DateTime.Now)
            {
                //Refresh the token
            }

            traktClient.Authorization = TraktAuthorization.CreateWith(access_token);
        }

        
    }
}