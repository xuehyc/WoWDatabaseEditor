using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace WDE.SqlQueryGenerator
{
    public struct SqlTimestamp
    {
        public readonly long Value;

        public SqlTimestamp(long value)
        {
            Value = value;
        }
    }

    public enum QueryInsertMode
    {
        Insert,
        InsertIgnore,
        Replace
    }

    public static class Extensions
    {
        public static IQuery InsertIgnore(this ITable table, Dictionary<string, object?> obj)
        {
            return table.Insert(obj, true);
        }
        
        public static IQuery InsertIgnore(this ITable table, object obj)
        {
            return table.Insert(obj, true);
        }
        
        public static IQuery Insert(this ITable table, Dictionary<string, object?> obj, bool insertIgnore = false)
        {
            return table.BulkInsert(new[] { obj }, insertIgnore ? QueryInsertMode.InsertIgnore : QueryInsertMode.Insert);
        }
        
        public static IQuery Insert(this ITable table, object obj, bool insertIgnore = false)
        {
            return table.BulkInsert(new[] { obj }, insertIgnore ? QueryInsertMode.InsertIgnore : QueryInsertMode.Insert);
        }
        
        public static IQuery Replace(this ITable table, Dictionary<string, object?> obj)
        {
            return table.BulkInsert(new[] { obj }, QueryInsertMode.Replace);
        }
        
        public static IQuery Replace(this ITable table, object obj)
        {
            return table.BulkInsert(new[] { obj }, QueryInsertMode.Replace);
        }

        public static IQuery BulkInsert(this ITable table, ICollection<Dictionary<string, object?>> objects, QueryInsertMode mode = QueryInsertMode.Insert)
        {
            bool first = true;
            IList<string> properties = null!;
            var sb = new StringBuilder();
            var lines = new List<string>();
            foreach (var o in objects)
            {
                if (first)
                {
                    properties = o.Keys.ToList();
                    var cols = string.Join(", ", properties.Select(c => $"`{c}`"));
                    var insert = mode == QueryInsertMode.Insert ? "INSERT" : (mode == QueryInsertMode.InsertIgnore ? "INSERT IGNORE" : "REPLACE");
                    sb.Append($"{insert} INTO `{table.TableName}` ({cols}) VALUES");
                    if (objects.Count > 1 || properties.Count > 1)
                        sb.AppendLine();
                    else
                        sb.Append(' ');
                    first = false;
                }

                var row = string.Join(", ", properties.Select(p => o[p].ToSql()));
                lines.Add($"({row})");
            }

            if (first)
                return new Query(table, "");

            sb.Append(string.Join("," + Environment.NewLine, lines));
            sb.Append(';');
            return new Query(table, sb.ToString());
        }

        public static IQuery BulkReplace(this ITable table, IEnumerable<object> objects)
        {
            return BulkInsert(table, objects, QueryInsertMode.Replace);
        }
        
        public static IQuery BulkInsert(this ITable table, IEnumerable<object> objects, QueryInsertMode mode = QueryInsertMode.Insert)
        {
            int i = 0;
            PropertyInfo[] properties = null!;
            var sb = new StringBuilder();
            var lines = new List<(string row, bool ignored, string? comment)>();
            PropertyInfo? commentProperty = null;
            PropertyInfo? ignoredProperty = null;
            foreach (var o in objects)
            {
                if (i == 0)
                {
                    var type = o.GetType();
                    commentProperty = type.GetProperty("__comment", BindingFlags.Instance | BindingFlags.Public);
                    ignoredProperty = type.GetProperty("__ignored", BindingFlags.Instance | BindingFlags.Public);
                    properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Where(prop => prop != commentProperty && prop != ignoredProperty)
                        .ToArray();
                    var cols = string.Join(", ", properties.Select(c => $"`{c.Name}`"));
                    var insert = mode == QueryInsertMode.Insert ? "INSERT" : (mode == QueryInsertMode.InsertIgnore ? "INSERT IGNORE" : "REPLACE");
                    sb.Append($"{insert} INTO `{table.TableName}` ({cols}) VALUES ");
                }
                else if (i == 1)
                {
                    sb.AppendLine();
                }

                var comment = (string?)commentProperty?.GetValue(o) ?? null;
                var ignored = (bool)((bool?)ignoredProperty?.GetValue(o) ?? false);
                var row = string.Join(", ", properties.Select(p => p.GetValue(o).ToSql()));
                lines.Add((row, ignored, comment));
                
                i++;
            }

            if (i == 0)
                return new Query(table, "");


            var lastNotIgnored = lines.FindLastIndex( row => !row.ignored);
            var lastIgnored = lines.FindLastIndex(row => row.ignored);
            
            for (var index = 0; index < lines.Count; index++)
            {
                var isLast = index == lines.Count - 1;
                var line = lines[index];

                if (line.ignored)
                    sb.Append(" -- ");
                
                sb.Append('(');
                sb.Append(line.Item1);
                sb.Append(')');
                
                if ((lastIgnored == -1 && !isLast) || (lastIgnored >= 0 && (lastIgnored < lastNotIgnored && !isLast) || (lastIgnored > lastNotIgnored && index < lastNotIgnored)))
                    sb.Append(',');
                else
                {
                    if (!line.ignored)
                        sb.Append(';');
                }
                
                if (line.comment != null)
                {
                    sb.Append(" -- ");
                    sb.Append(line.comment);
                }

                if (!isLast)
                    sb.AppendLine();
            }
            
            return new Query(table, sb.ToString());
        }
        
        public static IWhere Where(this ITable table, Expression<Func<IRow, bool>> predicate)
        {
            var condition = new ToSqlExpression().Visit(new SimplifyExpression().Visit(predicate.Body));
            if (condition is ConstantExpression c && c.Value is string s)
                return new Where(table, s);
            throw new Exception();
        }
        
        public static IWhere ToWhere(this ITable table)
        {
            return new Where(table);
        }

        public static IWhere Where(this IWhere where, Expression<Func<IRow, bool>> predicate)
        {
            var condition = new ToSqlExpression().Visit(new SimplifyExpression().Visit(predicate.Body));
            if (condition is ConstantExpression c && c.Value is string s)
                return new Where(where.Table, where.IsEmpty ? s : $"({where.Condition}) AND ({s})");
            throw new Exception();
        }

        public static IWhere OrWhere(this IWhere where, Expression<Func<IRow, bool>> predicate)
        {
            var condition = new ToSqlExpression().Visit(new SimplifyExpression().Visit(predicate.Body));
            if (condition is ConstantExpression c && c.Value is string s)
                return new Where(where.Table, where.IsEmpty ? s : $"({where.Condition}) OR ({s})");
            throw new Exception();
        }

        public static IWhere WhereIn<T>(this ITable table, string columnName, IEnumerable<T> values)
        {
            var str = string.Join(", ", values.Select(v => v.ToSql()));
            return new Where(table, $"`{columnName}` IN ({str})");
        }

        public static IWhere WhereIn<T>(this IWhere where, string columnName, IEnumerable<T> values, bool skipBrackets = false)
        {
            var str = string.Join(", ", values);
            if (skipBrackets)
            {
                var s = $"`{columnName}` IN ({str})";
                return new Where(where.Table, where.IsEmpty ? s : $"{where.Condition} AND {s}");
            }
            else
            {
                var s = $"`{columnName}` IN ({str})";
                return new Where(where.Table, where.IsEmpty ? s : $"({where.Condition}) AND ({s})");
            }
        }
        
        public static IQuery Delete(this IWhere query)
        {
            if (query.Condition == "1")
                return new Query(query.Table, $"DELETE FROM `{query.Table.TableName}`;");
            return new Query(query.Table, $"DELETE FROM `{query.Table.TableName}` WHERE {query.Condition};");
        }
        
        public static IQuery Select(this IWhere query)
        {
            if (query.Condition == "1")
                return new Query(query.Table, $"SELECT * FROM `{query.Table.TableName}`;");
            return new Query(query.Table, $"SELECT * FROM `{query.Table.TableName}` WHERE {query.Condition};");
        }
        
        public static IQuery Select(this IWhere query, params string[] columns)
        {
            if (query.Condition == "1")
                return new Query(query.Table, $"SELECT {string.Join(", ", columns)} FROM `{query.Table.TableName}`;");
            return new Query(query.Table, $"SELECT {string.Join(", ", columns)} FROM `{query.Table.TableName}` WHERE {query.Condition};");
        }

        public static IUpdateQuery ToUpdateQuery(this IWhere query)
        {
            return new UpdateQuery(query);
        }
        
        public static IUpdateQuery Set<T>(this IWhere query, string key, T? value)
        {
            return new UpdateQuery(query, key, value.ToSql());
        }
        
        public static IUpdateQuery Set<T>(this IUpdateQuery query, string key, T? value)
        {
            return new UpdateQuery(query, key, value.ToSql());
        }

        public static IQuery Update(this IUpdateQuery query, string? comment = null)
        {
            var upd = string.Join(", ", query.Updates.Select(pair => $"`{pair.Item1}` = {pair.Item2}"));
            string where = "";
            if (query.Condition.Condition != "1")
                where = $" WHERE {query.Condition.Condition}";
            return new Query(query.Condition.Table, $"UPDATE `{query.Condition.Table.TableName}` SET {upd}{where};" + (comment == null ? "" : " -- " + comment));
        }

        public static IQuery Comment(this IMultiQuery query, string comment)
        {
            return new Query(query, $" -- {comment}");
        }

        public static IQuery DefineVariable(this IMultiQuery query, string variableName, object? value)
        {
            return new Query(query, $"SET @{variableName} := {value.ToSql()};");
        }

        public static IQuery BlankLine(this IMultiQuery query)
        {
            return new BlankQuery(query);
        }

        public static IVariable Variable(this IMultiQuery query, string name)
        {
            return new Variable(name);
        }
        
        public static IRawText Raw(this IMultiQuery query, string text)
        {
            return new RawText(text);
        }
        
        public static string ToSql<T>(this T? o)
        {
            if (o is null)
                return "NULL";
            if (o is string s)
                return s.ToSqlEscapeString();
            if (o is float f)
                return f.ToString(CultureInfo.InvariantCulture);
            if (o is double d)
                return d.ToString(CultureInfo.InvariantCulture);
            if (o is int i)
                return i.ToString();
            if (o is uint ui)
                return ui.ToString();
            if (o is long l)
                return l.ToString();
            if (o is ulong ul)
                return ul.ToString();
            if (o is short sh)
                return sh.ToString();
            if (o is ushort ush)
                return ush.ToString();
            if (o is byte bt)
                return bt.ToString();
            if (o is sbyte sbt)
                return sbt.ToString();
            if (o is bool b)
                return b ? "1" : "0";
            if (o is DateTime dt)
                return dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss").ToSqlEscapeString();
            if (o is SqlTimestamp ts)
                return "FROM_UNIXTIME(" + ts.Value + ")";
            if (o is Guid g)
                return g.ToString().ToSqlEscapeString();
            if (o.GetType().IsEnum)
                return ((long)(object)o).ToString();
            if (o is RawText raw)
                return raw.ToString();
            if (o is Variable var)
                return var.ToString();
            throw new Exception($"Invalid type in ToSql: {o.GetType()}");
            return o.ToString() ?? "[INVALID TYPE]";
        }

        private static string StringQuotes = "'";
        
        internal static string ToSqlEscapeString(this string str)
        {
            return StringQuotes + str.Replace("\\", "\\\\").Replace(StringQuotes, "\\" + StringQuotes).Replace("\r", "").Replace("\n", "\\n") + StringQuotes;
        }
    }
}
