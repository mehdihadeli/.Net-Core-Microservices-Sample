using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Lockbox.Client.Extensions;
using Microsoft.AspNetCore.Hosting;
using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using System.Text;
using System.ComponentModel;
using Open.Serialization.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Common;
using Common.WebApi.Formatters;
using Scrutor;
using Common.WebApi.Exceptions;
using Common.WebApi.Requests;
using System.Threading.Tasks;
using System.Net;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Web;

namespace Common.WebApi
{
    public static class Extensions
    {
        private static readonly byte[] InvalidJsonRequestBytes = Encoding.UTF8.GetBytes("An invalid JSON was sent.");
        private const string WebApiSectionName = "WebApi";
        private const string AppsectionName = "App";
        private const string RegistryName = "WebApi";
        private const string EmptyJsonObject = "{}";
        private const string LocationHeader = "Location";
        private const string JsonContentType = "application/json";
        private static bool _bindRequestFromRoute;

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = { new StringEnumConverter(true) }
        };

        public static IApplicationBuilder UseApiEndpoints(this IApplicationBuilder app, Action<IEndpointsBuilder> build,
             Action<IApplicationBuilder> middleware = null)
        {
            var definitions = app.ApplicationServices.GetRequiredService<WebApiEndpointDefinitions>();
            middleware?.Invoke(app);

            app.UseEndpoints(router => build(new EndpointsBuilder(router, definitions)));

            return app;
        }

        [Description("By default Newtonsoft JSON serializer is being used and it sets Kestrel's and IIS ServerOptions AllowSynchronousIO = true")]
        public static void AddWebApi(this IServiceCollection serviceCollection, Action<IMvcCoreBuilder> configureMvc = null,
            IJsonSerializer jsonSerializer = null, string webApiSectionName = WebApiSectionName, string appSectionName = AppsectionName)
        {
            serviceCollection.AddSingleton<IServiceId, ServiceId>();
            serviceCollection.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            if (string.IsNullOrWhiteSpace(appSectionName))
            {
                appSectionName = AppsectionName;
            }

            var options = serviceCollection.GetOptions<AppOptions>(appSectionName);
            serviceCollection.AddMemoryCache();
            serviceCollection.AddSingleton(options);

            if (options.DisplayBanner && string.IsNullOrWhiteSpace(options.Name) == false)
            {
                var version = options.DisplayVersion ? $" {options.Version}" : string.Empty;
                Console.WriteLine(Figgle.FiggleFonts.Doom.Render($"{options.Name}{version}"));
            }

            if (jsonSerializer is null)
            {
                var factory = new Open.Serialization.Json.Newtonsoft.JsonSerializerFactory(new JsonSerializerSettings
                {
                    ContractResolver = new CamelCasePropertyNamesContractResolver(),
                    Converters = { new StringEnumConverter(true) }
                });
                jsonSerializer = factory.GetSerializer();
            }

            if (jsonSerializer.GetType().Namespace?.Contains("Newtonsoft") == true)
            {
                serviceCollection.Configure<KestrelServerOptions>(o => o.AllowSynchronousIO = true);
                serviceCollection.Configure<IISServerOptions>(o => o.AllowSynchronousIO = true);
            }

            serviceCollection.AddSingleton(jsonSerializer);
            serviceCollection.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            serviceCollection.AddSingleton(new WebApiEndpointDefinitions());
            var webApiOptions = serviceCollection.GetOptions<WebApiOptions>(webApiSectionName);
            serviceCollection.AddSingleton(options);
            _bindRequestFromRoute = webApiOptions.BindRequestFromRoute;

            var mvcCoreBuilder = serviceCollection
                .AddLogging()
                .AddMvcCore();

            mvcCoreBuilder.AddMvcOptions(o =>
                {
                    o.OutputFormatters.Clear();
                    o.OutputFormatters.Add(new JsonOutputFormatter(jsonSerializer));
                    o.InputFormatters.Clear();
                    o.InputFormatters.Add(new JsonInputFormatter(jsonSerializer));
                })
                .AddDataAnnotations()
                .AddApiExplorer()
                .AddAuthorization();

            configureMvc?.Invoke(mvcCoreBuilder);

            serviceCollection.Scan(s =>
                s.FromAssemblies(AppDomain.CurrentDomain.GetAssemblies())
                    .AddClasses(c => c.AssignableTo(typeof(IRequestHandler<,>)))
                    .AsImplementedInterfaces()
                    .WithTransientLifetime());

            serviceCollection.AddTransient<IRequestDispatcher, RequestDispatcher>();
            serviceCollection.AddErrorHandler<EmptyExceptionToResponseMapper>();
        }

        public static void AddErrorHandler<T>(this IServiceCollection serviceCollection)
            where T : class, IExceptionToResponseMapper
        {
            serviceCollection.AddTransient<ErrorHandlerMiddleware>();
            serviceCollection.AddSingleton<IExceptionToResponseMapper, T>();
        }

        public static IApplicationBuilder UseErrorHandler(this IApplicationBuilder builder)
        {
            builder.ApplicationServices.GetRequiredService<IExceptionToResponseMapper>();

            return builder.UseMiddleware<ErrorHandlerMiddleware>();
        }
        public static IApplicationBuilder UseServiceId(this IApplicationBuilder builder)
                  => builder.Map("/id", c => c.Run(async ctx =>
                  {
                      using (var scope = c.ApplicationServices.CreateScope())
                      {
                          var id = scope.ServiceProvider.GetService<IServiceId>().Id;
                          await ctx.Response.WriteAsync(id);
                      }
                  }));

        public static IApplicationBuilder UseAllForwardedHeaders(this IApplicationBuilder builder)
            => builder.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.All
            });

        public static Task<TResult> DispatchAsync<TRequest, TResult>(this HttpContext httpContext, TRequest request)
            where TRequest : class, IRequest
            => httpContext.RequestServices.GetService<IRequestHandler<TRequest, TResult>>().HandleAsync(request);

        public static T Bind<T>(this T model, Expression<Func<T, object>> expression, object value)
            => model.Bind<T, object>(expression, value);

        public static T BindId<T>(this T model, Expression<Func<T, Guid>> expression)
            => model.Bind(expression, Guid.NewGuid());

        public static T BindId<T>(this T model, Expression<Func<T, string>> expression)
            => model.Bind(expression, Guid.NewGuid().ToString("N"));

        private static TModel Bind<TModel, TProperty>(this TModel model, Expression<Func<TModel, TProperty>> expression,
            object value)
        {
            if (!(expression.Body is MemberExpression memberExpression))
            {
                memberExpression = ((UnaryExpression)expression.Body).Operand as MemberExpression;
            }

            if (memberExpression is null)
            {
                throw new InvalidOperationException("Invalid member expression.");
            }

            var propertyName = memberExpression.Member.Name.ToLowerInvariant();
            var modelType = model.GetType();
            var field = modelType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .SingleOrDefault(x => x.Name.ToLowerInvariant().StartsWith($"<{propertyName}>"));
            if (field is null)
            {
                return model;
            }

            field.SetValue(model, value);

            return model;
        }

        public static Task Ok(this HttpResponse response, object data = null)
        {
            response.StatusCode = 200;
            return data is null ? Task.CompletedTask : response.WriteJsonAsync(data);
        }

        public static Task Created(this HttpResponse response, string location = null, object data = null)
        {
            response.StatusCode = 201;
            if (string.IsNullOrWhiteSpace(location))
            {
                return Task.CompletedTask;
            }

            if (!response.Headers.ContainsKey(LocationHeader))
            {
                response.Headers.Add(LocationHeader, location);
            }

            return data is null ? Task.CompletedTask : response.WriteJsonAsync(data);
        }

        public static Task Accepted(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.Accepted;
            return Task.CompletedTask;
        }

        public static Task NoContent(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.NoContent;
            return Task.CompletedTask;
        }

        public static Task MovedPermanently(this HttpResponse response, string url)
        {
            response.StatusCode = (int)HttpStatusCode.MovedPermanently;
            if (!response.Headers.ContainsKey(LocationHeader))
            {
                response.Headers.Add(LocationHeader, url);
            }

            return Task.CompletedTask;
        }

        public static Task Redirect(this HttpResponse response, string url)
        {
            response.StatusCode = (int)HttpStatusCode.PermanentRedirect;
            if (!response.Headers.ContainsKey(LocationHeader))
            {
                response.Headers.Add(LocationHeader, url);
            }

            return Task.CompletedTask;
        }

        public static Task BadRequest(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return Task.CompletedTask;
        }

        public static Task Unauthorized(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return Task.CompletedTask;
        }

        public static Task Forbidden(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return Task.CompletedTask;
        }

        public static Task NotFound(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return Task.CompletedTask;
        }

        public static Task InternalServerError(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
            return Task.CompletedTask;
        }

        public static async Task WriteJsonAsync<T>(this HttpResponse response, T value)
        {
            response.ContentType = JsonContentType;
            var serializer = response.HttpContext.RequestServices.GetRequiredService<IJsonSerializer>();
            await serializer.SerializeAsync(response.Body, value);
        }

        public static async Task<T> ReadJsonAsync<T>(this HttpContext httpContext)
        {
            if (httpContext.Request.Body is null)
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.Body.WriteAsync(InvalidJsonRequestBytes, 0, InvalidJsonRequestBytes.Length);

                return default;
            }

            try
            {
                var request = httpContext.Request;
                var payload = await httpContext.RequestServices.GetRequiredService<IJsonSerializer>().DeserializeAsync<T>(request.Body);
                if (_bindRequestFromRoute && HasRouteData(request))
                {
                    var values = request.HttpContext.GetRouteData().Values;
                    foreach (var (key, value) in values)
                    {
                        var field = payload.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                            .SingleOrDefault(f => f.Name.ToLowerInvariant().StartsWith($"<{key}>",
                                StringComparison.InvariantCultureIgnoreCase));

                        if (field is null)
                        {
                            continue;
                        }

                        var fieldValue = TypeDescriptor.GetConverter(field.FieldType)
                            .ConvertFromInvariantString(value.ToString());
                        field.SetValue(payload, fieldValue);
                    }
                }

                var results = new List<ValidationResult>();
                if (Validator.TryValidateObject(payload, new ValidationContext(payload), results))
                {
                    return payload;
                }

                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteJsonAsync(results);

                return default;
            }
            catch
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.Body.WriteAsync(InvalidJsonRequestBytes, 0, InvalidJsonRequestBytes.Length);

                return default;
            }
        }

        public static T ReadQuery<T>(this HttpContext context) where T : class
        {
            var request = context.Request;
            RouteValueDictionary values = null;
            if (HasRouteData(request))
            {
                values = request.HttpContext.GetRouteData().Values;
            }

            if (HasQueryString(request))
            {
                var queryString = HttpUtility.ParseQueryString(request.HttpContext.Request.QueryString.Value);
                values ??= new RouteValueDictionary();
                foreach (var key in queryString.AllKeys)
                {
                    values.TryAdd(key, queryString[key]);
                }
            }

            var serializer = context.RequestServices.GetRequiredService<IJsonSerializer>();
            if (values is null)
            {
                return serializer.Deserialize<T>(EmptyJsonObject);
            }

            var serialized = serializer.Serialize(values.ToDictionary(k => k.Key, k => k.Value))
                .Replace("\\\"", "\"")
                .Replace("\"{", "{")
                .Replace("}\"", "}")
                .Replace("\"[", "[")
                .Replace("]\"", "]");

            return serializer.Deserialize<T>(serialized);
        }

        private static bool HasQueryString(this HttpRequest request)
            => request.Query.Any();

        private static bool HasRouteData(this HttpRequest request)
            => request.HttpContext.GetRouteData().Values.Any();

        public static string Args(this HttpContext context, string key)
            => context.Args<string>(key);

        public static T Args<T>(this HttpContext context, string key)
        {
            if (!context.GetRouteData().Values.TryGetValue(key, out var value))
            {
                return default;
            }

            if (typeof(T) == typeof(string) && value is string)
            {
                return (T)value;
            }

            var data = value?.ToString();
            if (string.IsNullOrWhiteSpace(data))
            {
                return default;
            }

            return (T)TypeDescriptor.GetConverter(typeof(T)).ConvertFromInvariantString(data);
        }

        private class EmptyExceptionToResponseMapper : IExceptionToResponseMapper
        {
            public ExceptionResponse Map(Exception exception) => null;
        }

    }

}