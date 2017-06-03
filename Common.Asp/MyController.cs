using Common.Asp.Util;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SqlTypes;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

namespace Common.Asp
{
    public class MyJsonResult : ActionResult
    {
        public Encoding ContentEncoding { get; set; }
        public string ContentType { get; private set; }
        public string JsonResponse { get; protected set; }
        public HttpStatusCode? StatusCode { get; private set; }
        public MyJsonResult(int StatusCode, string Data)
            : this((HttpStatusCode)StatusCode, Data)
        {
        }
        public MyJsonResult(HttpStatusCode StatusCode, string Data)
        {
            this.StatusCode = StatusCode;
            this.JsonResponse = Data;
        }

        protected void ExecuteResultMinusCache(ControllerContext context)
        {
            if (context == null)
                throw new ArgumentException("context");

            var response = context.HttpContext.Response;

            if (!string.IsNullOrEmpty(ContentType))
            {
                response.ContentType = ContentType;
            }
            else
            {
                response.ContentType = "application/json";
            }

            if (StatusCode.HasValue)
            {
                response.StatusCode = (int)StatusCode;
            }

            if (ContentEncoding != null)
            {
                response.ContentEncoding = ContentEncoding;
            }

            if (JsonResponse != null)
            {
                response.Write(JsonResponse);
            }
        }
        public override void ExecuteResult(ControllerContext context)
        {
            ExecuteResultMinusCache(context);

            var response = context.HttpContext.Response;
            response.AppendHeader("Cache-Control", "no-cache, no-store, must-revalidate"); // HTTP 1.1.
            response.AppendHeader("Pragma", "no-cache"); // HTTP 1.0.
            response.AppendHeader("Expires", "0"); // Proxies.
        }
    }
    public class CachableJsonResult : MyJsonResult
    {
        public DateTime LastModified { get; private set; }
        public CachableJsonResult(HttpRequestBase Request, DateTime LastModified, Func<string> GetData)
            : base(IsCacheValid(Request, LastModified) ? HttpStatusCode.NotModified : HttpStatusCode.OK, null)
        {
            if (StatusCode == HttpStatusCode.OK)
                JsonResponse = GetData();

            this.LastModified = LastModified;
        }
        private static bool IsCacheValid(HttpRequestBase Request, DateTime LastModified)
        {
            //var lastModified = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);

            var ifModifiedSince = Request.Headers["If-Modified-Since"];
            if (ifModifiedSince != null)
            {
                DateTime ifModifiedSinceDate;
                if (DateTime.TryParseExact(ifModifiedSince, "r", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out ifModifiedSinceDate) && ifModifiedSinceDate >= LastModified.AddMilliseconds(-LastModified.Millisecond))
                {
                    return true;
                }
            }

            return false;
        }

        public override void ExecuteResult(ControllerContext context)
        {
            var response = context.HttpContext.Response;
            response.Cache.SetCacheability(HttpCacheability.Public);
            response.Cache.SetLastModified(LastModified);

            switch (StatusCode)
            {
                case HttpStatusCode.OK:
                    ExecuteResultMinusCache(context);
                    break;
                default:
                    new HttpStatusCodeResult(StatusCode.Value).ExecuteResult(context);
                    break;
            }
        }
    }

    public class MyController : Controller
    {
        protected ViewResult InternalServerError()
        {
#if DEBUG
            throw new Exception();
#else
            //if (ViewBag.IsLoggedIn == null)
            //{
            //    await this.CheckLoginStatusAsync();
            //}
            return View("~/Views/Shared/InternalServerError.cshtml");
#endif
        }
        protected ViewResult NotFound()
        {
            if (ViewBag.IsLoggedIn == null)
            {
                //await this.CheckLoginStatusAsync();
            }
            return View("~/Views/Shared/404NotFound.cshtml");
        }

        protected const string InvalidJson = @"{""Error"":""Could not parse a simple JSON object""}";

        /// <summary>
        /// Executes an Stored Procedure in SQL Server.
        /// This method parses the Request's content for parameters to add to the SqlCommand, then adds extra output parameters for HttpStatusCode and JsonResponse.
        /// The function expects for the Request to be a simple JSON object, and requires for the Content-Type header to be set to 'application/json'
        /// 
        /// It returns a Json result. Often, this result will have have a single parameter "Error" to specify the error.
        /// </summary>
        /// <param name="StoredProcedureName">The name of the Stored Procedure to call.</param>
        /// <returns>A MyJsonResult with Json.</returns>
        protected async Task<MyJsonResult> ExecuteSqlForJson(string StoredProcedureName, bool ParseRequestBody = true, Action<SqlParameterCollection> AddParameters = null, object @lock = null, bool AddSessionInfo = true, string InputStreamString = null)
        {
            using (var sql = await SqlUtil.CreateSqlCommandAsync(StoredProcedureName))
            {
                // Now we're starting to parse the actual Json object
                var @params = sql.SqlCommand.Parameters;

                if (ParseRequestBody && (Request.HttpMethod == WebRequestMethods.Http.Post || Request.HttpMethod == WebRequestMethods.Http.Put))
                {
                    var contentType = Request.Headers["Content-Type"];
                    if (contentType != null && contentType.ToLowerInvariant().Contains("application/json"))
                    {
                        try
                        {
                            var jsonReader = new JsonTextReader(InputStreamString == null ? new StreamReader(Request.InputStream) as TextReader : new StringReader(InputStreamString));

                            // Start parsing the request.
                            using (jsonReader)
                            {
                                if (!jsonReader.Read() || jsonReader.TokenType != JsonToken.StartObject)
                                {
                                    return new MyJsonResult(HttpStatusCode.BadRequest, InvalidJson);
                                }

                                while (jsonReader.Read())
                                {
                                    switch (jsonReader.TokenType)
                                    {
                                        case JsonToken.PropertyName:
                                            var propertyName = jsonReader.Value as string;
                                            if (!jsonReader.Read())
                                            {
                                                return new MyJsonResult(HttpStatusCode.BadRequest, InvalidJson);
                                            }

                                            switch (jsonReader.TokenType)
                                            {
                                                case JsonToken.String:
                                                case JsonToken.Integer:
												case JsonToken.Boolean:
                                                case JsonToken.Float:
                                                    @params.AddWithValue(propertyName, jsonReader.Value);
                                                    break;
                                                case JsonToken.Date:
                                                    @params.AddWithValue(propertyName, ((DateTime)jsonReader.Value).ToString("o"));
                                                    break;
                                                case JsonToken.Null:
                                                    @params.AddWithValue(propertyName, DBNull.Value);
                                                    break;
                                                case JsonToken.StartArray:
                                                    if (jsonReader.Read())
                                                    {
                                                        switch (jsonReader.TokenType)
                                                        {
                                                            case JsonToken.String:
                                                            case JsonToken.Integer:
                                                            case JsonToken.Boolean:
                                                            case JsonToken.Float:
                                                            case JsonToken.Date:
                                                            case JsonToken.Null:
                                                                var token = jsonReader.TokenType;
                                                                var table = new DataTable();
                                                                @params.AddWithValue(propertyName, table).SqlDbType = SqlDbType.Structured;
                                                                table.Columns.Add("Value");
                                                                do
                                                                {
                                                                    if (token == JsonToken.Null && jsonReader.TokenType != JsonToken.Null)
                                                                        token = jsonReader.TokenType;
                                                                    if (jsonReader.TokenType == JsonToken.Null)
                                                                        table.Rows.Add(DBNull.Value);
                                                                    else if (jsonReader.TokenType == JsonToken.Date)
                                                                        table.Rows.Add(((DateTime)jsonReader.Value).ToString("o"));
                                                                    else
                                                                        table.Rows.Add(jsonReader.Value);
                                                                } while (jsonReader.Read() && ((token == JsonToken.Null && jsonReader.TokenType != JsonToken.EndArray) || jsonReader.TokenType == JsonToken.Null || token == jsonReader.TokenType || (token == JsonToken.Integer && jsonReader.TokenType == JsonToken.Float) || (token == JsonToken.Float && jsonReader.TokenType == JsonToken.Integer)));
                                                                if (jsonReader.TokenType != JsonToken.EndArray)
                                                                {
                                                                    return new MyJsonResult(HttpStatusCode.BadRequest, @"{""Error"":""If you pass an array of scalars, they must all be of the same type.""}");
                                                                }
                                                                break;
                                                            case JsonToken.StartObject:
                                                                // Add the table
                                                                @params.AddWithValue(propertyName, table = new DataTable()).SqlDbType = SqlDbType.Structured;
                                                                var columnIndices = new Dictionary<string, int>();

                                                                // Add the columns of the table.
                                                                using (var cmd = sql.SqlConnection.CreateCommand())
                                                                {
                                                                    cmd.CommandText = "SELECT c.name FROM sys.parameters a JOIN sys.table_types b ON a.user_type_id = b.user_type_id JOIN sys.columns c ON c.object_id = b.type_table_object_id WHERE a.object_id = object_id(@ProcedureName) AND a.name = N'@' + @PropertyName ORDER BY c.column_id";
                                                                    cmd.Parameters.AddWithValue("ProcedureName", StoredProcedureName);
                                                                    cmd.Parameters.AddWithValue("PropertyName", propertyName);

                                                                    using (var reader = await cmd.ExecuteReaderAsync())
                                                                    {
                                                                        for (int i = 0; await reader.ReadAsync(); i++)
                                                                        {
                                                                            var columnName = reader.GetString(0);
                                                                            table.Columns.Add(columnName);
                                                                            columnIndices[columnName] = i;
                                                                        }
                                                                    }
                                                                }
                                                                
                                                                var rowVals = new object[table.Columns.Count];

                                                                while (jsonReader.Read())
                                                                {
                                                                    if (jsonReader.TokenType == JsonToken.PropertyName)
                                                                    {
                                                                        var columnName = jsonReader.Value as string;

                                                                        if (!columnIndices.ContainsKey(columnName))
                                                                        {
                                                                            return new MyJsonResult(HttpStatusCode.BadRequest, $@"{{""Error"":""\""{columnName}\"" is not a valid property of \""{propertyName}.\""""}}");
                                                                        }

                                                                        var index = columnIndices[columnName];
                                                                        if (rowVals[index] != null)
                                                                        {
                                                                            return new MyJsonResult(HttpStatusCode.BadRequest, $@"{{""Error"":""You specified multiple values for \""{columnName}\"".""}}");
                                                                        }

                                                                        if (!jsonReader.Read())
                                                                            return new MyJsonResult(HttpStatusCode.BadRequest, InvalidJson);
                                                                        
                                                                        switch (jsonReader.TokenType)
                                                                        {
                                                                            case JsonToken.String:
                                                                            case JsonToken.Integer:
                                                                            case JsonToken.Float:
                                                                            case JsonToken.Boolean:
                                                                                rowVals[index] = jsonReader.Value;
                                                                                break;
                                                                            case JsonToken.Date:
                                                                                rowVals[index] = ((DateTime)jsonReader.Value).ToString("o");
                                                                                break;
                                                                            case JsonToken.Null:
                                                                                rowVals[index] = DBNull.Value;
                                                                                break;
                                                                            default:
                                                                                return new MyJsonResult(HttpStatusCode.BadRequest, @"{""Error"":""Values with type \'" + jsonReader.TokenType + @"\' are not supported.""}");
                                                                        }
                                                                    }
                                                                    else if (jsonReader.TokenType == JsonToken.EndObject)
                                                                    {
                                                                        for (int i = 0; i < rowVals.Length; i++)
                                                                        {
                                                                            if (rowVals[i] == null)
                                                                                rowVals[i] = DBNull.Value;
                                                                        }

                                                                        table.Rows.Add(rowVals);
                                                                        if (!jsonReader.Read())
                                                                            return new MyJsonResult(HttpStatusCode.BadRequest, InvalidJson);


                                                                        if (jsonReader.TokenType == JsonToken.StartObject)
                                                                        {
                                                                            rowVals = new object[rowVals.Length];
                                                                        }
                                                                        else if (jsonReader.TokenType == JsonToken.EndArray)
                                                                        {
                                                                            break;
                                                                        }
                                                                        else
                                                                        {
                                                                            return new MyJsonResult(HttpStatusCode.BadRequest, InvalidJson);
                                                                        }
                                                                    }
                                                                    else
                                                                    {
                                                                        return new MyJsonResult(HttpStatusCode.BadRequest, InvalidJson);
                                                                    }
                                                                }
                                                                break;
                                                            case JsonToken.EndArray:
                                                                break;
                                                            default:
                                                                return new MyJsonResult(HttpStatusCode.BadRequest, InvalidJson);
                                                        }
                                                    }

                                                    break;
                                                default:
                                                    return new MyJsonResult(HttpStatusCode.BadRequest, @"{""Error"":""Values with type \'" + jsonReader.TokenType + @"\' are not supported.""}");
                                            }
                                            break;
                                        case JsonToken.EndObject:
                                            if (jsonReader.Read())
                                            {
                                                return new MyJsonResult(HttpStatusCode.BadRequest, InvalidJson);
                                            }
                                            break;
                                        default:
                                            return new MyJsonResult(HttpStatusCode.BadRequest, InvalidJson);
                                    }
                                }
                            }
                        }
#if DEBUG
                        catch (JsonReaderException ex)
                        {
                            return new MyJsonResult(400, JsonConvert.SerializeObject(new { Error = "Could not parse the JSON." + (Request.IsLocal ? "\r\n\r\nDetails:\n" + ex.ToString() : "") }));
                        }
#else
                        catch (JsonReaderException)
                        {
                            return new MyJsonResult(HttpStatusCode.BadRequest, @"{""Error"":""Could not parse the JSON.""}");
                        }
#endif

                    }
                    else if (contentType != null && contentType.ToLowerInvariant().Contains("application/x-www-form-urlencoded"))
                    {
                        foreach (string key in Request.Form.Keys)
                        {
                            @params.AddWithValue(key, Request.Form[key]);
                        }
                    }
                    else
                    {
                        return new MyJsonResult(HttpStatusCode.UnsupportedMediaType, @"{""Error"":""Content-Type must be \""application\/json\"" or \""application\/x-www-form-urlencoded\""""}");
                    }
                }

                if (AddSessionInfo)
                    AddUserIdAndSessionId(@params);

                if (AddParameters != null)
                    AddParameters(@params);

#pragma warning disable CS0618 // We ignore the deprecation because this is in fact the method we want to call.
                return await ExecuteSqlForJson(sql, @params, @lock, AddSessionInfo);
#pragma warning restore CS0618
            }
        }
        [Obsolete("Use the ExecuteSqlForJson(string) method")]
        protected async Task<MyJsonResult> ExecuteSqlForJson(SqlUtil.ConnectionAndCommand sql, SqlParameterCollection @params, object @lock = null, bool AddSessionInfo = true)
        {
            var StatusCode = @params.Add("HttpStatusCode", SqlDbType.Int);
            var JsonResponse = @params.Add("JsonResponse", SqlDbType.NVarChar);
            JsonResponse.Size = -1;

            if (AddSessionInfo)
                AddUserIdAndSessionId(@params);
            
            StatusCode.Direction = JsonResponse.Direction = ParameterDirection.Output;
            sql.SqlCommand.CommandTimeout = 360000;

            try
            {
                if (@lock == null)
                {
                    await sql.SqlCommand.ExecuteNonQueryAsync();
                }
                else
                {
                    lock (@lock)
                    {
                        sql.SqlCommand.ExecuteNonQuery();
                    }
                }
            }
            catch (SqlException ex) when (MissingVariableRegex.IsMatch(ex.Message))
            {
                var missingVariable = MissingVariableRegex.Matches(ex.Message)[0].Groups[1];
                return new MyJsonResult(HttpStatusCode.BadRequest, JsonConvert.SerializeObject(new { Error = $"The parameter \"{missingVariable}\" is a required input." }));
            }
            catch (SqlException ex) when (ExtraVariableRegex.IsMatch(ex.Message))
            {
                var extraVariable = ExtraVariableRegex.Matches(ex.Message)[0].Groups[1];
                return new MyJsonResult(HttpStatusCode.BadRequest, JsonConvert.SerializeObject(new { Error = $"The parameter called \"{extraVariable}\" is invalid for this endpoint." }));
            }
            catch (SqlException ex) when (ExtraVariableRegex3.IsMatch(ex.Message))
            {
                var extraVariable = ExtraVariableRegex3.Matches(ex.Message)[0].Groups[1];
                return new MyJsonResult(HttpStatusCode.BadRequest, JsonConvert.SerializeObject(new { Error = $"The parameter called \"{extraVariable}\" is invalid for this endpoint." }));
            }
            catch (SqlException ex) when (ExtraVariableRegex2.IsMatch(ex.Message))
            {
                // We have to figure out on our own what extra parameter was passed.
                var procedure = ex.Procedure;
                var parametersSent = sql.SqlCommand.Parameters.OfType<SqlParameter>().Select(p => p.ParameterName).ToList();
                sql.SqlCommand.Parameters.Clear();
                sql.SqlCommand.Parameters.AddWithValue("ProcedureName", ex.Procedure);

                sql.SqlCommand.CommandType = CommandType.Text;
                sql.SqlCommand.CommandText = "SELECT REPLACE(name,N'@',N'') FROM sys.parameters WHERE object_id = object_id(@ProcedureName);"; //AND name NOT IN(N'@JsonResponse', N'@HttpStatusCode')

                var availableParameters = new List<string>();
                using (var reader = await sql.SqlCommand.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        availableParameters.Add(reader.GetString(0));
                    }
                }

                var extraVariables = parametersSent.Except(availableParameters).ToList();
                switch (extraVariables.Count)
                {
                    case 0:
                        return new MyJsonResult(HttpStatusCode.BadRequest, JsonConvert.SerializeObject(new { Error = $"The following parameters were duplicates: {string.Join(", ", parametersSent.GroupBy(p => p).Where(p => p.Count() > 1).Select(p => $"\"{p.Key}\""))}" }));
                    case 1:
                        return new MyJsonResult(HttpStatusCode.BadRequest, JsonConvert.SerializeObject(new { Error = $"The parameter called \"{extraVariables[0]}\" is invalid for this endpoint." }));
                    default:
                        return new MyJsonResult(HttpStatusCode.BadRequest, JsonConvert.SerializeObject(new { Error = $"The following parameters are invalid for this endpoint: {string.Join(", ", extraVariables.Select(p => $"\"{p}\""))}" }));
                }
            }

            if ((int)StatusCode.Value == 500)
                throw new Exception(JsonResponse.Value as string);
            return new MyJsonResult((int)StatusCode.Value, JsonResponse.Value as string);
        }
        private static readonly Regex MissingVariableRegex = new Regex(@"^Procedure or function '[a-zA-Z_][a-zA-Z0-9_]*' expects parameter '@([a-zA-Z_][a-zA-Z0-9_]*)', which was not supplied.$");
        private static readonly Regex ExtraVariableRegex = new Regex(@"^The procedure ""[a-zA-Z_][a-zA-Z0-9_]*"" has no parameter named ""@([a-zA-Z_][a-zA-Z0-9_]*)"".$");
        private static readonly Regex ExtraVariableRegex2 = new Regex(@"^Procedure or function [a-zA-Z_][a-zA-Z0-9_]* has too many arguments specified.$");
        private static readonly Regex ExtraVariableRegex3 = new Regex(@"^@([a-zA-Z_][a-zA-Z0-0_]*) is not a parameter for procedure [a-zA-Z_][a-zA-Z0-9_]*.$");

        protected void AddUserIdAndSessionId(SqlParameterCollection @params)
        {
            var uId = Request.Cookies["UserId"];
            var sId = Request.Cookies["SessionId"];

            int UserId;
            Guid SessionId;

            if (!@params.Contains("UserId") && uId != null && int.TryParse(uId.Value, out UserId))
                @params.AddWithValue("UserId", UserId);

            if (!@params.Contains("SessionId") && sId != null && Guid.TryParse(sId.Value, out SessionId))
                @params.AddWithValue("SessionId", SessionId);
        }

        [Obsolete("Use one of the overloads for `ExecuteSqlForJson`")]
        protected async Task<ActionResult> SelectAjaxValues(string CommandFormat, HashSet<string> LowerCaseColumnNames, int? EventId, int? draw, int? start, int? length, SearchObject search, OrderObject[] order, ColumnsObject[] columns)
        {
            if (!EventId.HasValue)
            {
                return new MyJsonResult(HttpStatusCode.NotFound, @"{""error"":""EventId is required""}");
            }
            if (!draw.HasValue)
            {
                return new MyJsonResult(HttpStatusCode.BadRequest, @"{""error"":""draw is required""}");
            }
            if (!start.HasValue)
            {
                return new MyJsonResult(HttpStatusCode.BadRequest, @"{""error"":""start is required""}");
            }
            if (!length.HasValue)
            {
                return new MyJsonResult(HttpStatusCode.BadRequest, @"{""error"":""length is required""}");
            }

            search.regex = bool.Parse(Request.QueryString["search[regex]"]);
            search.value = Request.QueryString["search[value]"];

            for (int i = 0; i < order.Length; i++)
            {
                order[i].column = int.Parse(Request.QueryString["order[" + i + "][column]"]);
                order[i].dir = Request.QueryString["order[" + i + "][dir]"];
            }
            for (int i = 0; i < columns.Length; i++)
            {
                columns[i].data = Request.QueryString["columns[" + i + "][data]"];
                columns[i].name = Request.QueryString["columns[" + i + "][name]"];
                columns[i].orderable = bool.Parse(Request.QueryString["columns[" + i + "][orderable]"]);
                columns[i].search = new SearchObject
                {
                    value = Request.QueryString["columns[" + i + "][search][value]"],
                    regex = bool.Parse(Request.QueryString["columns[" + i + "][search][regex]"]),
                };
                columns[i].searchable = bool.Parse(Request.QueryString["columns[" + i + "][searchable]"]);
            }

            var OrderBy = new StringBuilder();

            if (columns.Any(p => !LowerCaseColumnNames.Contains(p.name.ToLowerInvariant())))
            {
                return new MyJsonResult(HttpStatusCode.BadRequest, @"{""error"":""Invalid column name""}");
            }

            foreach (var ord in order)
            {
                if (OrderBy.Length != 0)
                {
                    OrderBy.Append(", ");
                }
                var column = columns[ord.column];

                switch (ord.dir.ToLowerInvariant())
                {
                    case "asc":
                    case "desc":
                        break;
                    default:
                        return new MyJsonResult(HttpStatusCode.BadRequest, @"{""error"":""Invalid sort direction""}");
                }

                OrderBy.Append(column.name)
                    .Append(' ')
                    .Append(ord.dir);
            }

            using (var sql = await SqlUtil.CreateSqlCommandAsync())
            {
                var @params = sql.SqlCommand.Parameters;

                var WhereClause = new StringBuilder();
                var words = search.value.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < words.Length; i++)
                {
                    if (WhereClause.Length == 0)
                    {
                        WhereClause.Append(@" WHERE ");
                    }
                    else
                    {
                        WhereClause.Append(" AND ");
                    }

                    WhereClause.Append("SearchQuery LIKE '%' + REPLACE(REPLACE(REPLACE(REPLACE(@WhereVar").Append(i).Append(@",N'\',N'\\'),N'%',N'\%'),N'_',N'\_'),N'[',N'\[') + N'%' ESCAPE N'\'");
                    @params.AddWithValue("WhereVar" + i, words[i]);
                }

                var command = string.Format(CommandFormat, OrderBy, WhereClause, draw, length == -1 ? "" : string.Format(" WHERE RowNumber BETWEEN {0} AND {1}", start, start + length));

                sql.SqlCommand.CommandType = CommandType.Text;

                AddUserIdAndSessionId(@params);
                sql.SqlCommand.CommandText = command;

                @params.AddWithValue("EventId", EventId);

                return new MyJsonResult(HttpStatusCode.OK, await sql.SqlCommand.ExecuteScalarAsync() as string);
            }
        }


        [Obsolete("Use ExecuteSqlForJson, passing in a string.")]
        protected async Task<ActionResult> SelectAjaxValues(string objectName)
        {
            using (var sql = await SqlUtil.CreateSqlCommandAsync("SELECT dbo.WebGet" + objectName + "(@UserId, @SessionId)", CommandType.Text))
            {
                AddUserIdAndSessionId(sql.SqlCommand.Parameters);

                var result = new MyJsonResult(HttpStatusCode.OK, await sql.SqlCommand.ExecuteScalarAsync() as string);

                return result;
            }
        }
    }
    public struct SearchObject
    {
        public string value { get; set; }
        public bool regex { get; set; }

    }
    public struct OrderObject
    {
        public int column { get; set; }
        public string dir { get; set; }
    }
    public struct ColumnsObject
    {
        public string data { get; set; }
        public string name { get; set; }
        public bool searchable { get; set; }
        public bool orderable { get; set; }
        public SearchObject search { get; set; }
    }
}
