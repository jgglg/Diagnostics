// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNet.Builder;
using Microsoft.AspNet.Diagnostics.Entity.FunctionalTests.Helpers;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.TestHost;
using Microsoft.Data.Entity;
using Microsoft.Data.Entity.Infrastructure;
using Microsoft.Data.Entity.Internal;
using Microsoft.Data.Entity.Migrations;
using Microsoft.Data.Entity.Migrations.Infrastructure;
using Microsoft.Data.Entity.Storage;
using Microsoft.Data.Entity.Utilities;
using Microsoft.Framework.DependencyInjection;
using Microsoft.Framework.Logging;
using Xunit;
using Microsoft.AspNet.Diagnostics.Entity.Tests.Helpers;

namespace Microsoft.AspNet.Diagnostics.Entity.Tests
{
    public class DatabaseErrorPageMiddlewareTest
    {
        [Fact]
        public async Task Successful_requests_pass_thru()
        {
            TestServer server = TestServer.Create(app => app
                .UseDatabaseErrorPage()
                .UseMiddleware<SuccessMiddleware>());

            HttpResponseMessage response = await server.CreateClient().GetAsync("http://localhost/");

            Assert.Equal("Request Handled", await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        class SuccessMiddleware
        {
            public SuccessMiddleware(RequestDelegate next)
            { }

            public virtual async Task Invoke(HttpContext context)
            {
                await context.Response.WriteAsync("Request Handled");
                context.Response.StatusCode = (int)HttpStatusCode.OK;
            }
        }

        [Fact]
        public async Task Non_database_exceptions_pass_thru()
        {
            TestServer server = TestServer.Create(app => app
                .UseDatabaseErrorPage()
                .UseMiddleware<ExceptionMiddleware>());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await server.CreateClient().GetAsync("http://localhost/"));

            Assert.Equal("Exception requested from TestMiddleware", ex.Message);
        }

        class ExceptionMiddleware
        {
            public ExceptionMiddleware(RequestDelegate next)
            { }

            public virtual Task Invoke(HttpContext context)
            {
                throw new InvalidOperationException("Exception requested from TestMiddleware");
            }
        }

        [Fact]
        public async Task Error_page_displayed_no_migrations()
        {
            TestServer server = SetupTestServer<BloggingContext, NoMigrationsMiddleware>();
            HttpResponseMessage response = await server.CreateClient().GetAsync("http://localhost/");

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains(StringsHelpers.GetResourceString("FormatDatabaseErrorPage_NoDbOrMigrationsTitle", typeof(BloggingContext).Name), content);
            Assert.Contains(StringsHelpers.GetResourceString("FormatDatabaseErrorPage_AddMigrationCommand").Replace(">", "&gt;"), content);
            Assert.Contains(StringsHelpers.GetResourceString("FormatDatabaseErrorPage_ApplyMigrationsCommand").Replace(">", "&gt;"), content);
        }

        class NoMigrationsMiddleware
        {
            public NoMigrationsMiddleware(RequestDelegate next)
            { }

            public virtual Task Invoke(HttpContext context)
            {
                using (var db = context.ApplicationServices.GetService<BloggingContext>())
                {
                    db.Blogs.Add(new Blog());
                    db.SaveChanges();
                    throw new Exception("SaveChanges should have thrown");
                }
            }
        }

        [Fact]
        public async Task Error_page_displayed_pending_migrations()
        {
            TestServer server = SetupTestServer<BloggingContextWithMigrations, PendingMigrationsMiddleware>();
            HttpResponseMessage response = await server.CreateClient().GetAsync("http://localhost/");

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains(StringsHelpers.GetResourceString("FormatDatabaseErrorPage_PendingMigrationsTitle", typeof(BloggingContextWithMigrations).Name), content);
            Assert.Contains(StringsHelpers.GetResourceString("FormatDatabaseErrorPage_ApplyMigrationsCommand").Replace(">", "&gt;"), content);
            Assert.Contains("<li>111111111111111_MigrationOne</li>", content);
            Assert.Contains("<li>222222222222222_MigrationTwo</li>", content);

            Assert.DoesNotContain(StringsHelpers.GetResourceString("FormatDatabaseErrorPage_AddMigrationCommand").Replace(">", "&gt;"), content);
        }

        class PendingMigrationsMiddleware
        {
            public PendingMigrationsMiddleware(RequestDelegate next)
            { }

            public virtual Task Invoke(HttpContext context)
            {
                using (var db = context.ApplicationServices.GetService<BloggingContextWithMigrations>())
                {
                    db.Blogs.Add(new Blog());
                    db.SaveChanges();
                    throw new Exception("SaveChanges should have thrown");
                }
            }
        }

        [Fact]
        public async Task Error_page_displayed_pending_model_changes()
        {
            TestServer server = SetupTestServer<BloggingContextWithPendingModelChanges, PendingModelChangesMiddleware>();
            HttpResponseMessage response = await server.CreateClient().GetAsync("http://localhost/");

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains(StringsHelpers.GetResourceString("FormatDatabaseErrorPage_PendingChangesTitle", typeof(BloggingContextWithPendingModelChanges).Name), content);
            Assert.Contains(StringsHelpers.GetResourceString("FormatDatabaseErrorPage_AddMigrationCommand").Replace(">", "&gt;"), content);
            Assert.Contains(StringsHelpers.GetResourceString("FormatDatabaseErrorPage_ApplyMigrationsCommand").Replace(">", "&gt;"), content);
        }

        class PendingModelChangesMiddleware
        {
            public PendingModelChangesMiddleware(RequestDelegate next)
            { }

            public virtual Task Invoke(HttpContext context)
            {
                using (var db = context.ApplicationServices.GetService<BloggingContextWithPendingModelChanges>())
                {
                    db.Database.ApplyMigrations();

                    db.Blogs.Add(new Blog());
                    db.SaveChanges();
                    throw new Exception("SaveChanges should have thrown");
                }
            }
        }

        [Fact]
        public async Task Pass_thru_when_context_not_in_services()
        {
            using (var database = SqlServerTestStore.CreateScratch())
            {
                var logProvider = new TestLoggerProvider();

                var server = TestServer.Create(app =>
                {
                    app.UseDatabaseErrorPage();
                    app.UseMiddleware<ContextNotRegisteredInServicesMiddleware>();
                    app.ApplicationServices.GetService<ILoggerFactory>().AddProvider(logProvider);
                },
                services =>
                {
                    services.AddEntityFramework().AddSqlServer();
                    var optionsBuilder = new DbContextOptionsBuilder();
                    if (!PlatformHelper.IsMono)
                    {
                        optionsBuilder.UseSqlServer(database.ConnectionString);
                    }
                    else
                    {
                        optionsBuilder.UseInMemoryDatabase();
                    }
                    services.AddInstance<DbContextOptions>(optionsBuilder.Options);
                });

                var ex = await Assert.ThrowsAsync<SqlException>(async () =>
                    await server.CreateClient().GetAsync("http://localhost/"));

                Assert.True(logProvider.Logger.Messages.Any(m =>
                    m.StartsWith(StringsHelpers.GetResourceString("FormatDatabaseErrorPageMiddleware_ContextNotRegistered", typeof(BloggingContext)))));
            }
        }

        class ContextNotRegisteredInServicesMiddleware
        {
            public ContextNotRegisteredInServicesMiddleware(RequestDelegate next)
            { }

            public virtual Task Invoke(HttpContext context)
            {
                var options = context.ApplicationServices.GetService<DbContextOptions>();
                using (var db = new BloggingContext(context.ApplicationServices, options))
                {
                    db.Blogs.Add(new Blog());
                    db.SaveChanges();
                    throw new Exception("SaveChanges should have thrown");
                }
            }
        }

        [Fact]
        public async Task Pass_thru_when_exception_in_logic()
        {
            using (var database = SqlServerTestStore.CreateScratch())
            {
                var logProvider = new TestLoggerProvider();

                var server = SetupTestServer<BloggingContextWithSnapshotThatThrows, ExceptionInLogicMiddleware>(logProvider);

                var ex = await Assert.ThrowsAsync<SqlException>(async () =>
                    await server.CreateClient().GetAsync("http://localhost/"));

                Assert.True(logProvider.Logger.Messages.Any(m =>
                    m.StartsWith(StringsHelpers.GetResourceString("FormatDatabaseErrorPageMiddleware_Exception"))));
            }
        }

        class ExceptionInLogicMiddleware
        {
            public ExceptionInLogicMiddleware(RequestDelegate next)
            { }

            public virtual Task Invoke(HttpContext context)
            {
                using (var db = context.ApplicationServices.GetService<BloggingContextWithSnapshotThatThrows>())
                {
                    db.Blogs.Add(new Blog());
                    db.SaveChanges();
                    throw new Exception("SaveChanges should have thrown");
                }
            }
        }

        [Fact]
        public async Task Error_page_displayed_when_exception_wrapped()
        {
            TestServer server = SetupTestServer<BloggingContext, WrappedExceptionMiddleware>();
            HttpResponseMessage response = await server.CreateClient().GetAsync("http://localhost/");

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("I wrapped your exception", content);
            Assert.Contains(StringsHelpers.GetResourceString("FormatDatabaseErrorPage_NoDbOrMigrationsTitle", typeof(BloggingContext).Name), content);
        }

        class WrappedExceptionMiddleware
        {
            public WrappedExceptionMiddleware(RequestDelegate next)
            { }

            public virtual Task Invoke(HttpContext context)
            {
                using (var db = context.ApplicationServices.GetService<BloggingContext>())
                {
                    db.Blogs.Add(new Blog());
                    try
                    {
                        db.SaveChanges();
                        throw new Exception("SaveChanges should have thrown");
                    }
                    catch (Exception ex)
                    {
                        throw new Exception("I wrapped your exception", ex);
                    }
                }
            }
        }

        private static TestServer SetupTestServer<TContext, TMiddleware>(ILoggerProvider logProvider = null)
            where TContext : DbContext
        {
            using (var database = SqlServerTestStore.CreateScratch())
            {
                return TestServer.Create(app =>
                {
                    app.UseDatabaseErrorPage();

                    app.UseMiddleware<TMiddleware>();

                    if (logProvider != null)
                    {
                        app.ApplicationServices.GetService<ILoggerFactory>().AddProvider(logProvider);
                    }
                },
                services =>
                {
                    services.AddEntityFramework()
                        .AddSqlServer();

                    services.AddScoped<TContext>();

                    var optionsBuilder = new DbContextOptionsBuilder();
                    if (!PlatformHelper.IsMono)
                    {
                        optionsBuilder.UseSqlServer(database.ConnectionString);
                    }
                    else
                    {
                        optionsBuilder.UseInMemoryDatabase();
                    }
                    services.AddInstance(optionsBuilder.Options);
                });
            }
        }
    }
}