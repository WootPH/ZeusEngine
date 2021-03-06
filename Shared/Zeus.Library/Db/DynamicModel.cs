using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Data.Common;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Text;
using Zeus.Library.Extensions;

namespace Zeus.Library.Db {

    /// <summary>
    ///     Model base class for all Zeus model objects
    /// </summary>
    public class DynamicModel : DynamicObject {

        /// <summary>
        ///     A connection string like: server=localhost;user id=root;persist security info=True;database=zeus
        /// </summary>
        protected readonly string _connectionString;

        /// <summary>
        ///     A reference on the used db factory
        /// </summary>
        protected readonly DbProviderFactory _dbProviderFactory;

        /// <summary>
        ///     A cache of metadata which is lazy-loaded on <see cref="Schema" /> property
        /// </summary>
        protected IEnumerable<dynamic> _schemaCache;

        /// <summary>
        ///     The name of acolumn which describes one row in <see cref="TableName" />
        /// </summary>
        public virtual string DescriptorField { get; protected set; }

        /// <summary>
        ///     Contains the list of errors from the last failed transaction
        /// </summary>
        public IList<string> Errors { get; protected set; }

        /// <summary>
        ///     The name of the primary-key column in <see cref="TableName" />
        /// </summary>
        public virtual string PrimaryKeyField { get; protected set; }

        /// <summary>
        ///     Creates an empty Expando set with defaults from the DB
        ///     @TODO: Test a default object instance vs a default database entry - in Utopia this should be equal
        /// </summary>
        public dynamic Prototype {
            get {
                dynamic result = new ExpandoObject();
                var dc = (IDictionary<string, object>)result;
                var schema = Schema;
                foreach (var column in schema) {
                    dc.Add(column.COLUMN_NAME, DefaultValue(column));
                }
                result._Table = this;
                return result;
            }
        }

        /// <summary>
        ///     List out all the schema bits for use with ... whatever
        /// </summary>
        public IEnumerable<dynamic> Schema {
            get { return _schemaCache ?? (_schemaCache = Query("SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @0", TableName)); }
        }

        /// <summary>
        ///     The name of the database table of this model
        /// </summary>
        public virtual string TableName { get; protected set; }


        public DynamicModel(string connectionStringName, string tableName = "", string primaryKeyField = "", string descriptorField = "") {
            Errors = new List<string>();

            if (string.IsNullOrEmpty(tableName) == false) {
                TableName = tableName;
            }
            if (string.IsNullOrEmpty(primaryKeyField) == false) {
                PrimaryKeyField = primaryKeyField;
            }
            if (string.IsNullOrEmpty(descriptorField) == false) {
                DescriptorField = descriptorField;
            }

            var providerName = "System.Data.SqlClient";
            if (ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName != null) {
                providerName = ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName;
            }

            _dbProviderFactory = DbProviderFactories.GetFactory(providerName);
            _connectionString = ConfigurationManager.ConnectionStrings[connectionStringName].ConnectionString;
        }

        public DynamicModel(ConnectionStringSettings settings, string providerName = "System.Data.SqlClient") {
            Errors = new List<string>();

            _dbProviderFactory = DbProviderFactories.GetFactory(providerName);
            _connectionString = settings.ConnectionString;
        }

        public DynamicModel(ConnectionStringSettings settings, DbProviderFactory factory) {
            Errors = new List<string>();

            _dbProviderFactory = factory;
            _connectionString = settings.ConnectionString;
        }


        /// <summary>
        ///     Creates a new Expando from a Form POST - white listed against the columns
        ///     in the DB
        /// </summary>
        public dynamic CreateFrom(NameValueCollection coll) {
            dynamic result = new ExpandoObject();
            var dc = (IDictionary<string, object>)result;
            var enumerable = Schema as IList<dynamic> ?? Schema.ToList();
            // loop the collection, setting only what's in the Schema
            foreach (var item in coll.Keys) {
                var exists = enumerable.Any(x => x.COLUMN_NAME.ToLower() == item.ToString().ToLower());
                if (!exists) {
                    continue;
                }

                var key = item.ToString();
                var val = coll[key];
                dc.Add(key, val);
            }
            return result;
        }

        /// <summary>
        ///     Gets a default value for the <paramref name="column" />
        /// </summary>
        public dynamic DefaultValue(dynamic column) {
            string def = column.COLUMN_DEFAULT;
            if (String.IsNullOrEmpty(def)) {
                return null;
            }

            dynamic result;
            switch (def) {
                case "(getdate())":
                case "getdate()":
                    result = DateTime.Now.ToShortDateString();
                    break;
                case "newid()":
                    result = Guid.NewGuid().ToString();
                    break;
                default:
                    result = def.Replace("(", "").Replace(")", "");
                    break;
            }

            return result;
        }

        /// <summary>
        ///     Enumerates the reader yielding the result - thanks to Jeroen Haegebaert
        /// </summary>
        public virtual IEnumerable<dynamic> Query(string sql, params object[] args) {
            var result = new List<dynamic>();
            using (var conn = OpenConnection()) {
                var rdr = CreateCommand(sql, conn, args).ExecuteReader();
                while (rdr.Read()) {
                    result.Add(rdr.RecordToExpando());
                }
            }
            return result;
        }

        public virtual IEnumerable<dynamic> Query(string sql, DbConnection connection, params object[] args) {
            using (var rdr = CreateCommand(sql, connection, args).ExecuteReader()) {
                while (rdr.Read()) {
                    yield return rdr.RecordToExpando();
                }
            }
        }

        /// <summary>
        ///     Returns a single result
        /// </summary>
        public virtual object Scalar(string sql, params object[] args) {
            object result;
            using (var conn = OpenConnection()) {
                result = CreateCommand(sql, conn, args).ExecuteScalar();
            }
            return result;
        }

        /// <summary>
        ///     Creates a DBCommand that you can use for loving your database.
        /// </summary>
        protected DbCommand CreateCommand(string sql, DbConnection conn, params object[] args) {
            var result = _dbProviderFactory.CreateCommand();
            if (result == null) {
                throw new Exception("Failed to create command using factory provider");
            }

            result.Connection = conn;
#if DEBUG
            Debug.WriteLine("[DynamicModel] Execute sql: {0}{1}{0} Arguments:{0}{2}", Environment.NewLine, sql, args);
#endif
            result.CommandText = sql;
            if (args.Length > 0) {
                result.AddParams(args);
            }
            return result;
        }

        /// <summary>
        ///     Returns and <see cref="OpenConnection" />
        /// </summary>
        public virtual DbConnection OpenConnection() {
            var result = _dbProviderFactory.CreateConnection();
            if (result == null) {
                throw new Exception("Failed to create command using factory provider");
            }

            result.ConnectionString = _connectionString;
            result.Open();
            return result;
        }

        /// <summary>
        ///     Builds a set of <see cref="Insert" /> and <see cref="Update" /> commands
        ///     based on the passed-on objects. These objects can be POCOs, Anonymous,
        ///     NameValueCollections, or Expandos. Objects With a PK property (whatever
        ///     <see cref="PrimaryKeyField" /> is set to) will be created at UPDATEs
        /// </summary>
        public virtual List<DbCommand> BuildCommands(params object[] things) {
            var commands = new List<DbCommand>();
            foreach (var item in things) {
                if (HasPrimaryKey(item)) {
                    commands.Add(CreateUpdateCommand(item.ToExpando(), GetPrimaryKey(item)));
                } else {
                    commands.Add(CreateInsertCommand(item.ToExpando()));
                }
            }
            return commands;
        }


        public virtual int Execute(DbCommand command) {
            return Execute(new[] {
                command
            });
        }

        public virtual int Execute(string sql, params object[] args) {
            return Execute(CreateCommand(sql, null, args));
        }

        /// <summary>
        ///     Executes a series of DBCommands in a transaction
        /// </summary>
        public virtual int Execute(IEnumerable<DbCommand> commands) {
            var result = 0;
            using (var conn = OpenConnection()) {
                using (var tx = conn.BeginTransaction()) {
                    foreach (var cmd in commands) {
                        cmd.Connection = conn;
                        cmd.Transaction = tx;
                        result += cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
            return result;
        }

        /// <summary>
        ///     Conventionally introspects the object passed in for a field that looks
        ///     like a PK. If you've named your PrimaryKeyField, this becomes easy
        /// </summary>
        public virtual bool HasPrimaryKey(object o) {
            return o.ToDictionary().ContainsKey(PrimaryKeyField);
        }

        /// <summary>
        ///     If the object passed in has a property with the same name as your
        ///     <see cref="PrimaryKeyField" /> it is returned here.
        /// </summary>
        public virtual object GetPrimaryKey(object o) {
            object result;
            o.ToDictionary().TryGetValue(PrimaryKeyField, out result);
            return result;
        }

        /// <summary>
        ///     Returns all records complying with the passed-in WHERE clause and
        ///     arguments, ordered as specified, limited (TOP) by limit.
        /// </summary>
        public virtual IEnumerable<dynamic> All(string where = "", string join = "", string orderBy = "", int limit = 0, string columns = "*",
            params object[] args) {
            var sql = BuildSelect(join, where, orderBy, limit);
            return Query(string.Format(sql, columns, TableName), args);
        }

        protected virtual string BuildSelect(string join, string where, string orderBy, int limit) {
            var sql = limit > 0 ? "SELECT TOP " + limit + " {0} FROM {1} " : "SELECT {0} FROM {1} ";
            if (string.IsNullOrEmpty(join) == false) {
                sql += " " + join;
            }
            if (!string.IsNullOrEmpty(where)) {
                sql += " " + (where.Trim().StartsWith("where", StringComparison.OrdinalIgnoreCase) ? where : "WHERE " + where);
            }
            if (!String.IsNullOrEmpty(orderBy)) {
                sql += " " + (orderBy.Trim().StartsWith("order by", StringComparison.OrdinalIgnoreCase) ? orderBy : "ORDER BY " + orderBy);
            }
            return sql;
        }

        /// <summary>
        ///     Returns a dynamic PagedResult. Result properties are Items, TotalPages,
        ///     and TotalRecords.
        /// </summary>
        public virtual dynamic Paged(string where = "", string orderBy = "", string columns = "*", int pageSize = 20, int currentPage = 1, params object[] args) {
            return BuildPagedResult(@where: where, orderBy: orderBy, columns: columns, pageSize: pageSize, currentPage: currentPage, args: args);
        }

        public virtual dynamic Paged(string sql, string primaryKey, string where = "", string orderBy = "", string columns = "*", int pageSize = 20,
            int currentPage = 1, params object[] args) {
            return BuildPagedResult(sql, primaryKey, where, orderBy, columns, pageSize, currentPage, args);
        }

        protected dynamic BuildPagedResult(string sql = "", string primaryKeyField = "", string where = "", string orderBy = "", string columns = "*",
            int pageSize = 20, int currentPage = 1, params object[] args) {
            dynamic result = new ExpandoObject();
            string countSql;
            if (!string.IsNullOrEmpty(sql)) {
                countSql = string.Format("SELECT COUNT({0}) FROM ({1}) AS PagedTable", primaryKeyField, sql);
            } else {
                countSql = string.Format("SELECT COUNT({0}) FROM {1}", PrimaryKeyField, TableName);
            }

            if (String.IsNullOrEmpty(orderBy)) {
                orderBy = string.IsNullOrEmpty(primaryKeyField) ? PrimaryKeyField : primaryKeyField;
            }

            if (!string.IsNullOrEmpty(where)) {
                if (!where.Trim().StartsWith("where", StringComparison.CurrentCultureIgnoreCase)) {
                    where = " WHERE " + where;
                }
            }

            string query;
            if (!string.IsNullOrEmpty(sql)) {
                query = string.Format("SELECT {0} FROM (SELECT ROW_NUMBER() OVER (ORDER BY {1}) AS Row, {0} FROM ({2}) AS PagedTable {3}) AS Paged ", columns,
                    orderBy, sql, where);
            } else {
                query = string.Format("SELECT {0} FROM (SELECT ROW_NUMBER() OVER (ORDER BY {1}) AS Row, {0} FROM {2} {3}) AS Paged ", columns, orderBy,
                    TableName, where);
            }

            var pageStart = (currentPage - 1) * pageSize;
            query += string.Format(" WHERE Row > {0} AND Row <={1}", pageStart, (pageStart + pageSize));
            countSql += where;
            result.TotalRecords = Scalar(countSql, args);
            result.TotalPages = result.TotalRecords / pageSize;
            if (result.TotalRecords % pageSize > 0) {
                result.TotalPages += 1;
            }
            result.Items = Query(string.Format(query, columns, TableName), args);
            return result;
        }

        /// <summary>
        ///     Returns a single row from the database
        /// </summary>
        public virtual dynamic Single(string where, params object[] args) {
            var sql = string.Format("SELECT * FROM {0} WHERE {1}", TableName, where);
            return Query(sql, args).FirstOrDefault();
        }

        /// <summary>
        ///     Returns a single row from the database
        /// </summary>
        public virtual dynamic Single(object key, string columns = "*") {
            var sql = string.Format("SELECT {0} FROM {1} WHERE {2} = @0", columns, TableName, PrimaryKeyField);
            return Query(sql, key).FirstOrDefault();
        }

        /// <summary>
        ///     This will return a string/object dictionary for dropdowns etc
        /// </summary>
        public virtual IDictionary<string, object> KeyValues(string orderBy = "") {
            if (String.IsNullOrEmpty(DescriptorField)) {
                throw new InvalidOperationException("There's no DescriptorField set - do this in your constructor to describe the text value you want to see");
            }
            var sql = string.Format("SELECT {0},{1} FROM {2} ", PrimaryKeyField, DescriptorField, TableName);
            if (!String.IsNullOrEmpty(orderBy)) {
                sql += "ORDER BY " + orderBy;
            }

            var results = Query(sql).ToList().Cast<IDictionary<string, object>>();
            return results.ToDictionary(key => key[PrimaryKeyField].ToString(), value => value[DescriptorField]);
        }

        /// <summary>
        ///     This will return an Expando as a Dictionary
        /// </summary>
        public virtual IDictionary<string, object> ItemAsDictionary(ExpandoObject item) {
            return item;
        }

        // Checks to see if a key is present based on the passed-in value
        public virtual bool ItemContainsKey(string key, ExpandoObject item) {
            var dc = ItemAsDictionary(item);
            return dc.ContainsKey(key);
        }

        /// <summary>
        ///     Executes a set of objects as <see cref="Insert" /> or <see cref="Update" />
        ///     commands based on their property settings, within a transaction. These
        ///     objects can be POCOs, Anonymous, NameValueCollections, or Expandos.
        ///     Objects With a PK property (whatever <see cref="PrimaryKeyField" /> is set
        ///     to) will be created at UPDATEs
        /// </summary>
        public virtual int Save(params object[] things) {
            if (things.Any(item => IsValid(item) == false)) {
                throw new InvalidOperationException("Can't save this item: " + String.Join("; ", Errors.ToArray()));
            }
            var commands = BuildCommands(things);
            return Execute(commands);
        }

        public virtual DbCommand CreateInsertCommand(dynamic expando) {
            DbCommand result;
            var settings = (IDictionary<string, object>)expando;
            var sbKeys = new StringBuilder();
            var sbVals = new StringBuilder();
            var stub = "INSERT INTO {0} ({1}) \r\n VALUES ({2})";
            result = CreateCommand(stub, null);
            var counter = 0;
            foreach (var item in settings) {
                sbKeys.AppendFormat("{0},", item.Key);
                sbVals.AppendFormat("@{0},", counter);
                result.AddParam(item.Value);
                counter++;
            }
            if (counter <= 0) {
                throw new InvalidOperationException("Can't parse this object to the database - there are no properties set");
            }

            var keys = sbKeys.ToString().Substring(0, sbKeys.Length - 1);
            var vals = sbVals.ToString().Substring(0, sbVals.Length - 1);
            var sql = string.Format(stub, TableName, keys, vals);
            result.CommandText = sql;
            return result;
        }

        /// <summary>
        ///     Creates a command for use with transactions - <see langword="internal" />
        ///     stuff mostly, but here for you to play with
        /// </summary>
        public virtual DbCommand CreateUpdateCommand(dynamic expando, object key) {
            var settings = (IDictionary<string, object>)expando;
            var sbKeys = new StringBuilder();
            var stub = "UPDATE {0} SET {1} WHERE {2} = @{3}";
            var result = CreateCommand(stub, null);
            var counter = 0;
            foreach (var item in settings) {
                var val = item.Value;
                if (!item.Key.Equals(PrimaryKeyField, StringComparison.OrdinalIgnoreCase) && item.Value != null) {
                    result.AddParam(val);
                    sbKeys.AppendFormat("{0} = @{1}, \r\n", item.Key, counter);
                    counter++;
                }
            }
            if (counter <= 0) {
                throw new InvalidOperationException("No parsable object was sent in - could not divine any name/value pairs");
            }

            // add the key
            result.AddParam(key);
            // strip the last commas
            var keys = sbKeys.ToString().Substring(0, sbKeys.Length - 4);
            result.CommandText = string.Format(stub, TableName, keys, PrimaryKeyField, counter);
            return result;
        }

        /// <summary>
        ///     Removes one or more records from the DB according to the passed-in WHERE
        /// </summary>
        public virtual DbCommand CreateDeleteCommand(string where = "", object key = null, params object[] args) {
            var sql = string.Format("DELETE FROM {0} ", TableName);
            if (key != null) {
                sql += string.Format("WHERE {0}=@0", PrimaryKeyField);
                args = new[] {
                    key
                };
            } else if (!string.IsNullOrEmpty(where)) {
                sql += where.Trim().StartsWith("where", StringComparison.OrdinalIgnoreCase) ? where : "WHERE " + where;
            }
            return CreateCommand(sql, null, args);
        }

        public bool IsValid(dynamic item) {
            Errors.Clear();
            Validate(item);
            return Errors.Count == 0;
        }


        /// <summary>
        ///     Adds a record to the database. You can pass in an Anonymous object, an
        ///     ExpandoObject, A regular old POCO, or a NameValueColletion from a
        ///     Request.Form or Request.QueryString
        /// </summary>
        public virtual dynamic Insert(object o) {
            var ex = o.ToExpando();
            if (!IsValid(ex)) {
                throw new InvalidOperationException("Can't insert: " + String.Join("; ", Errors.ToArray()));
            }

            if (BeforeSave(ex) == false) {
                return null;
            }

            using (dynamic conn = OpenConnection()) {
                var cmd = CreateInsertCommand(ex);
                cmd.Connection = conn;
                cmd.ExecuteNonQuery();
                cmd.CommandText = "SELECT @@IDENTITY as newID";
                ((IDictionary<string, object>)ex)[PrimaryKeyField] = cmd.ExecuteScalar();
                Inserted(ex);
            }
            return ex;
        }

        /// <summary>
        ///     Updates a record in the database. You can pass in an Anonymous object, an
        ///     ExpandoObject, A regular old POCO, or a NameValueCollection from a
        ///     Request.Form or Request.QueryString
        /// </summary>
        public virtual int Update(object o, object key) {
            var ex = o.ToExpando();
            if (!IsValid(ex)) {
                throw new InvalidOperationException("Can't Update: " + String.Join("; ", Errors.ToArray()));
            }
            var result = 0;
            if (BeforeSave(ex) == false) {
                return result;
            }

            result = Execute(CreateUpdateCommand(ex, key));
            Updated(ex);
            return result;
        }

        /// <summary>
        ///     Removes one or more records from the DB according to the passed-in WHERE
        /// </summary>
        public int Delete(object key = null, string where = "", params object[] args) {
            var deleted = Single(key);
            var result = 0;
            if (BeforeDelete(deleted) == false) {
                return result;
            }

            result = Execute(CreateDeleteCommand(@where, key, args));
            Deleted(deleted);
            return result;
        }

        public void DefaultTo(string key, object value, dynamic item) {
            if (ItemContainsKey(key, item)) {
                return;
            }

            var dc = (IDictionary<string, object>)item;
            dc[key] = value;
        }

        public virtual void Validate(dynamic item) {
        }

        public virtual void Inserted(dynamic item) {
        }

        public virtual void Updated(dynamic item) {
        }

        public virtual void Deleted(dynamic item) {
        }

        public virtual bool BeforeDelete(dynamic item) {
            return true;
        }

        public virtual bool BeforeSave(dynamic item) {
            return true;
        }

        // validation methods

        public virtual void ValidatesPresenceOf(object value, string message = "Required") {
            if (value == null) {
                Errors.Add(message);
            } else if (String.IsNullOrEmpty(value.ToString())) {
                Errors.Add(message);
            }
        }

        // fun methods

        public virtual void ValidatesNumericalityOf(object value, string message = "Should be a number") {
            var type = value.GetType().Name;
            var numerics = new[] {
                "Int32", "Int16", "Int64", "Decimal", "Double", "Single", "Float"
            };
            if (!numerics.Contains(type)) {
                Errors.Add(message);
            }
        }

        public virtual void ValidateIsCurrency(object value, string message = "Should be money") {
            decimal val;
            if (value == null) {
                Errors.Add(message);
            } else if (decimal.TryParse(value.ToString(), out val) == false || val == decimal.MinValue) {
                Errors.Add(message);
            }
        }

        public int Count() {
            return Count(TableName);
        }

        public int Count(string tableName, string where = "", params object[] args) {
            return (int)Scalar("SELECT COUNT(*) FROM " + tableName + " " + where, args);
        }

        /// <summary>
        ///     A helpful query tool
        /// </summary>
        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result) {
            // parse the method
            var constraints = new List<string>();
            var counter = 0;
            var info = binder.CallInfo;
            // accepting named args only... SKEET!
            if (info.ArgumentNames.Count != args.Length) {
                throw new InvalidOperationException("Please use named arguments for this type of query - the column name, orderby, columns, etc");
            }
            // first should be "FindBy, Last, Single, First"
            var op = binder.Name;
            var columns = " * ";
            var orderBy = string.Format(" ORDER BY {0}", PrimaryKeyField);
            var where = "";
            var whereArgs = new List<object>();

            // loop the named args - see if we have order, columns and constraints
            if (info.ArgumentNames.Count > 0) {
                for (var i = 0; i < args.Length; i++) {
                    var name = info.ArgumentNames[i].ToLower();
                    switch (name) {
                        case "orderby":
                            orderBy = " ORDER BY " + args[i];
                            break;
                        case "columns":
                            columns = args[i].ToString();
                            break;
                        default:
                            constraints.Add(string.Format(" {0} = @{1}", name, counter));
                            whereArgs.Add(args[i]);
                            counter++;
                            break;
                    }
                }
            }

            // Build the WHERE bits
            if (constraints.Count > 0) {
                where = " WHERE " + string.Join(" AND ", constraints.ToArray());
            }
            // probably a bit much here but... yeah this whole thing needs to be refactored...
            if (op.ToLower() == "count") {
                result = Scalar("SELECT COUNT(*) FROM " + TableName + where, whereArgs.ToArray());
            } else if (op.ToLower() == "sum") {
                result = Scalar("SELECT SUM(" + columns + ") FROM " + TableName + where, whereArgs.ToArray());
            } else if (op.ToLower() == "max") {
                result = Scalar("SELECT MAX(" + columns + ") FROM " + TableName + where, whereArgs.ToArray());
            } else if (op.ToLower() == "min") {
                result = Scalar("SELECT MIN(" + columns + ") FROM " + TableName + where, whereArgs.ToArray());
            } else if (op.ToLower() == "avg") {
                result = Scalar("SELECT AVG(" + columns + ") FROM " + TableName + where, whereArgs.ToArray());
            } else {
                // build the SQL
                var sql = "SELECT TOP 1 " + columns + " FROM " + TableName + @where;
                var justOne = op.StartsWith("First") || op.StartsWith("Last") || op.StartsWith("Get") || op.StartsWith("Single");

                // Be sure to sort by DESC on the PK (PK Sort is the default)
                if (op.StartsWith("Last")) {
                    orderBy = orderBy + " DESC ";
                } else {
                    // default to multiple
                    sql = "SELECT " + columns + " FROM " + TableName + where;
                }

                if (justOne) {
                    // return a single record
                    result = Query(sql + orderBy, whereArgs.ToArray()).FirstOrDefault();
                } else {
                    // return lots
                    result = Query(sql + orderBy, whereArgs.ToArray());
                }
            }
            return true;
        }

    }

}