﻿using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.TestUtilities.Xunit;
using System;
using System.Linq;
using Xunit;

namespace Testcase_MergeInto
{
    public class RankCache
    {
        public int ContestId { get; set; }
        public int TeamId { get; set; }
        public int PointsRestricted { get; set; }
        public int TotalTimeRestricted { get; set; }
        public int PointsPublic { get; set; }
        public int TotalTimePublic { get; set; }
    }

    public class RankSource
    {
        public int ContestId { get; set; }
        public int TeamId { get; set; }
        public int Time { get; set; }
        public bool Public { get; set; }
    }

    public class MergeContext : DbContext
    {
        public DbSet<RankCache> RankCache { get; set; }

        public DbSet<RankSource> RankSource { get; set; }

        public string DefaultSchema { get; }

        public MergeContext(string schema, DbContextOptions options)
            : base(options)
        {
            DefaultSchema = schema;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RankCache>(entity =>
            {
                entity.ToTable(nameof(RankCache) + "_" + DefaultSchema);

                entity.HasKey(e => new { e.ContestId, e.TeamId });
            });

            modelBuilder.Entity<RankSource>(entity =>
            {
                entity.ToTable(nameof(RankSource) + "_" + DefaultSchema);

                entity.HasKey(e => new { e.ContestId, e.TeamId });
            });
        }
    }

    public sealed class NameFixture : IDisposable
    {
        public string Schema { get; }

        public Func<MergeContext> ContextFactory { get; }

        public NameFixture()
        {
            ContextFactory = ContextUtil.MakeContextFactory<MergeContext>();
            using var context = ContextFactory();
            context.EnsureContext();

            context.RankSource.AddRange(
                new RankSource { ContestId = 2, TeamId = 1, Public = true, Time = 100 },
                new RankSource { ContestId = 1, TeamId = 2, Public = false, Time = 77 });

            context.RankCache.AddRange(
                new RankCache
                {
                    TeamId = 2,
                    ContestId = 1,
                    PointsPublic = 1,
                    PointsRestricted = 1,
                    TotalTimePublic = 9,
                    TotalTimeRestricted = 9
                },
                new RankCache
                {
                    TeamId = 3,
                    ContestId = 1,
                    PointsPublic = 1,
                    PointsRestricted = 1,
                    TotalTimePublic = 9,
                    TotalTimeRestricted = 9
                });
            context.SaveChanges();
        }

        public void Dispose()
        {
            using var context = ContextFactory();
            context.DropContext();
        }
    }

    [Collection("DatabaseCollection")]
    [DatabaseProviderSkipCondition(DatabaseProvider.PostgreSQL)]
    public sealed class MergeIntoSql : IClassFixture<NameFixture>
    {
        readonly Func<MergeContext> contextFactory;

        public MergeIntoSql(NameFixture nameFixture)
        {
            contextFactory = nameFixture.ContextFactory;
        }

        [ConditionalFact, TestPriority(0)]
        public void Upsert()
        {
            using var context = contextFactory();

            var ot = new[]
            {
                new { ContestId = 1, TeamId = 2, Time = 50 },
                new { ContestId = 3, TeamId = 4, Time = 50 },
            };

            var ans = context.RankCache.Merge(
                sourceTable: ot,
                targetKey: rc => new { rc.ContestId, rc.TeamId },
                sourceKey: rc => new { rc.ContestId, rc.TeamId },
                updateExpression:
                    (rc, rc2) => new RankCache
                    {
                        PointsPublic = rc.PointsPublic + 1,
                        TotalTimePublic = rc.TotalTimePublic + rc2.Time,
                    },
                insertExpression:
                    rc2 => new RankCache
                    {
                        PointsPublic = 1,
                        PointsRestricted = 1,
                        TotalTimePublic = rc2.Time,
                        TotalTimeRestricted = rc2.Time,
                        ContestId = rc2.ContestId,
                        TeamId = rc2.TeamId,
                    },
                delete: false);

            Assert.Equal(3, context.RankCache.Count());
        }

        [ConditionalFact, TestPriority(1)]
        public void Synchronize()
        {
            using var context = contextFactory();

            var ans = context.RankCache.Merge(
                sourceTable: context.RankSource,
                targetKey: rc => new { rc.ContestId, rc.TeamId },
                sourceKey: rc => new { rc.ContestId, rc.TeamId },
                updateExpression:
                    (rc, rc2) => new RankCache
                    {
                        PointsPublic = rc2.Public ? rc.PointsPublic + 1 : rc.PointsPublic,
                        TotalTimePublic = rc2.Public ? rc.TotalTimePublic + rc2.Time : rc.TotalTimePublic,
                        PointsRestricted = rc.PointsRestricted + 1,
                        TotalTimeRestricted = rc.TotalTimeRestricted + rc2.Time,
                    },
                insertExpression:
                    rc2 => new RankCache
                    {
                        PointsPublic = rc2.Public ? 1 : 0,
                        PointsRestricted = 1,
                        TotalTimePublic = rc2.Public ? rc2.Time : 0,
                        TotalTimeRestricted = rc2.Time,
                        ContestId = rc2.ContestId,
                        TeamId = rc2.TeamId,
                    },
                delete: true);

            var contents = context.RankCache
                .OrderBy(rc => rc.ContestId)
                .ThenBy(rc => rc.TeamId)
                .ToList();
            Assert.Equal(2, contents.Count);
            Assert.Equal(86, contents[0].TotalTimeRestricted);
            Assert.Equal(100, contents[1].TotalTimeRestricted);
        }

        [ConditionalFact, TestPriority(2)]
        [DatabaseProviderSkipCondition(DatabaseProvider.InMemory)]
        public void SourceFromSql()
        {
            using var context = contextFactory();

            var fromSql = context.RankSource.ToSQL();

            var ans = context.RankCache.Merge(
                sourceTable: context.RankSource.FromSqlRaw(fromSql),
                targetKey: rc => new { rc.ContestId, rc.TeamId },
                sourceKey: rc => new { rc.ContestId, rc.TeamId },
                updateExpression:
                    (rc, rc2) => new RankCache
                    {
                        PointsPublic = rc2.Public ? rc.PointsPublic + 1 : rc.PointsPublic,
                        TotalTimePublic = rc2.Public ? rc.TotalTimePublic + rc2.Time : rc.TotalTimePublic,
                        PointsRestricted = rc.PointsRestricted + 1,
                        TotalTimeRestricted = rc.TotalTimeRestricted + rc2.Time,
                    },
                insertExpression:
                    rc2 => new RankCache
                    {
                        PointsPublic = rc2.Public ? 1 : 0,
                        PointsRestricted = 1,
                        TotalTimePublic = rc2.Public ? rc2.Time : 0,
                        TotalTimeRestricted = rc2.Time,
                        ContestId = rc2.ContestId,
                        TeamId = rc2.TeamId,
                    },
                delete: true);
        }
    }
}
