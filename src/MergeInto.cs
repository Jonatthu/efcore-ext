﻿using Microsoft.EntityFrameworkCore.Bulk;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace Microsoft.EntityFrameworkCore
{
    public static class MergeIntoExtensions
    {
        public static int Merge<TTarget, TSource, TJoinKey>(
            this DbSet<TTarget> targetTable,
            IEnumerable<TSource> sourceTable,
            Expression<Func<TTarget, TJoinKey>> targetKey,
            Expression<Func<TSource, TJoinKey>> sourceKey,
            Expression<Func<TTarget, TSource, TTarget>> updateExpression,
            Expression<Func<TSource, TTarget>> insertExpression,
            bool delete)
            where TTarget : class
            where TSource : class
        {
            var context = targetTable.GetDbContext();
            var (sql, parameters) = GetSqlMerge(context, targetTable, sourceTable, targetKey, sourceKey, updateExpression, insertExpression, delete);
            return context.Database.ExecuteSqlRaw(sql, parameters);
        }


        public static async Task<int> MergeAsync<TTarget, TSource, TJoinKey>(
            this DbSet<TTarget> targetTable,
            IEnumerable<TSource> sourceTable,
            Expression<Func<TTarget, TJoinKey>> targetKey,
            Expression<Func<TSource, TJoinKey>> sourceKey,
            Expression<Func<TTarget, TSource, TTarget>> updateExpression,
            Expression<Func<TSource, TTarget>> insertExpression,
            bool delete)
            where TTarget : class
            where TSource : class
        {
            var context = targetTable.GetDbContext();
            var (sql, parameters) = GetSqlMerge(context, targetTable, sourceTable, targetKey, sourceKey, updateExpression, insertExpression, delete);
            return await context.Database.ExecuteSqlRawAsync(sql, parameters).ConfigureAwait(false);
        }


        private static (string, IEnumerable<object>) GetSqlMerge<TTarget, TSource, TJoinKey>(
            DbContext context,
            DbSet<TTarget> targetTable,
            IEnumerable<TSource> sourceTable2,
            Expression<Func<TTarget, TJoinKey>> targetKey,
            Expression<Func<TSource, TJoinKey>> sourceKey,
            Expression<Func<TTarget, TSource, TTarget>> updateExpression,
            Expression<Func<TSource, TTarget>> insertExpression,
            bool delete)
            where TTarget : class
            where TSource : class
        {
            var sourceQuery = sourceTable2 as IQueryable<TSource>;
            List<TSource> sourceTable = null;

            if (sourceQuery == null)
            {
                if (!typeof(TSource).IsAnonymousType())
                    throw new InvalidOperationException();
                sourceTable = sourceTable2.ToList();

                if (sourceTable.Count == 0)
                    return (delete
                        ? $"TRUNCATE TABLE [{context.Model.FindEntityType(typeof(TTarget)).GetTableName()}]"
                        : $"SELECT 0", Array.Empty<object>());

                var selector = Expression.Lambda<Func<TTarget, TSource>>(
                    body: AnonymousObjectExpressionFactory.Create(typeof(TSource)),
                    Expression.Parameter(typeof(TTarget), "t"));
                sourceQuery = targetTable.Select(selector).Distinct();
            }

            var query = targetTable
                .IgnoreQueryFilters()
                .Join(
                    inner: sourceQuery,
                    outerKeySelector: targetKey,
                    innerKeySelector: sourceKey,
                    resultSelector: MergeResult(updateExpression, insertExpression));

            var entityType = context.Model.FindEntityType(typeof(TTarget));
            var execution = TranslationStrategy.Go(query);
            var selectExpression = execution.SelectExpression;
            var queryContext = execution.QueryContext;

            // TODO: This logic will be changed after the fix of table-splitting in EFCore 5.0 preview
            if (selectExpression.Tables.Count != 2
                || selectExpression.Predicate != null
                || !(selectExpression.Tables[1] is InnerJoinExpression innerJoin)
                || !(selectExpression.Tables[0] is TableExpression table))
                throw new NotSupportedException("Unknown entity configured.");

            var exp = new MergeExpression
            {
                JoinPredicate = innerJoin.JoinPredicate,
                TargetTable = table,
                SourceTable = innerJoin.Table,
                Limit = selectExpression.Limit,
                NotMatchedBySource = delete,
                Matched = updateExpression == null ? null : new List<ProjectionExpression>(),
                NotMatchedByTarget = insertExpression == null ? null : new List<ProjectionExpression>()
            };

            if (sourceTable != null)
            {
                if (!(innerJoin.Table is SelectExpression subquery))
                    throw new InvalidOperationException("Translate failed.");
                var props = typeof(TSource).GetProperties();

                var projects =
                    (from p in subquery.Projection
                     let c = (SqlConstantExpression)p.Expression
                     let i = c.ReadBack()
                     select new { p, c.TypeMapping, i, a = props[i] }).ToList();
                projects.Sort((a, b) => a.i.CompareTo(b.i));
                if (projects.Last().i + 1 != projects.Count)
                    throw new InvalidOperationException("Translate failed.");
                var items = new SqlParameterExpression[sourceTable.Count, props.Length];

                for (int i = 0; i < sourceTable.Count; i++)
                {
                    for (int j = 0; j < props.Length; j++)
                    {
                        var paraName = $"__ap_{queryContext.ParameterValues.Count}";
                        queryContext.AddParameter(paraName, props[j].GetValue(sourceTable[i]));
                        items[i, j] = Expression.Parameter(props[j].PropertyType, paraName).ToSql(projects[j].TypeMapping);
                    }
                }

                exp.TableChanges = subquery;
                exp.SourceTable = new ValuesExpression(items, projects.Select(a => a.a.Name), subquery.Alias);
                exp.ColumnChanges = projects.ToDictionary(a => a.p.Alias, a => a.a.Name);
            }

            var columns = entityType.GetColumns();
            var map = EnhancedQuerySqlGenerator._getMapper(selectExpression);
            foreach (var (a, b) in map)
            {
                if (!(b is ConstantExpression constant))
                    throw new NotSupportedException("Wrong query.");
                var name = a.ToString();
                if (name.StartsWith("Insert.") || name.StartsWith("Update."))
                    ((List<ProjectionExpression>)(name.StartsWith("Insert.") ? exp.NotMatchedByTarget : exp.Matched))
                        .Add(new ProjectionExpression(
                            selectExpression.Projection[(int)constant.Value].Expression,
                            alias: columns[name[7..]]));
                else
                    throw new InvalidOperationException("Translate failed.");
            }

            var command = execution.GetMergeCommand(exp);
            var parameters = execution.CreateParameter(command);
            return (command.CommandText, parameters);
        }

        private class Result<T>
        {
            public T Insert { get; set; }

            public T Update { get; set; }
        }

        private static Expression<Func<T1, T2, Result<T1>>> MergeResult<T1, T2>(
            Expression<Func<T1, T2, T1>> updateExpression,
            Expression<Func<T2, T1>> insertExpression)
        {
            var para1 = Expression.Parameter(typeof(T1), "t");
            var para2 = Expression.Parameter(typeof(T2), "s");
            var res = Expression.New(typeof(Result<T1>));
            var binding = Array.Empty<MemberBinding>().AsEnumerable();

            if (updateExpression != null)
                binding = binding.Append(Expression.Bind(
                    member: typeof(Result<T1>).GetProperty("Update"),
                    expression: new ParameterReplaceVisitor(
                        (updateExpression.Parameters[0], para1),
                        (updateExpression.Parameters[1], para2))
                    .Visit(updateExpression.Body)));

            if (insertExpression != null)
                binding = binding.Append(Expression.Bind(
                    member: typeof(Result<T1>).GetProperty("Insert"),
                    expression: new ParameterReplaceVisitor(
                        (insertExpression.Parameters[0], para2))
                    .Visit(insertExpression.Body)));

            var body = Expression.MemberInit(res, binding);
            return Expression.Lambda<Func<T1, T2, Result<T1>>>(body, para1, para2);
        }
    }
}
