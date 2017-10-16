using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace Common.Asp.Util
{
    public static class SqlUtil
    {
        private static string ConnectionString = ConfigurationManager.ConnectionStrings["connection-string"]?.ConnectionString;

        /// <summary>
        /// A wrapper around an SqlConnection and SqlCommand object. If this wrapper or either of the objects are disposed, all three will be disposed.
        /// </summary>
        public sealed class ConnectionAndCommand : IDisposable
        {
            /// <summary>
            /// Stores the SqlConnection and creates an SqlCommand with it.
            /// </summary>
            /// <param name="Connection">The SqlConnection. This connection needs to already be opened.</param>
            /// <param name="CommandText">The initial CommandText to set for the SqlCommand that is to be created.</param>
            /// <param name="CommandType">The CommandType to set for the SqlCommand that is to be created.</param>
            internal ConnectionAndCommand(SqlConnection Connection, string CommandText, CommandType CommandType)
            {
                this.SqlConnection = Connection;

                this.SqlCommand = new SqlCommand(CommandText, Connection) { CommandType = CommandType };

                // We create a handler that will dispose this object when either the Connection or the Command are disposed.
                DisposedHandler = (a, b) => Dispose();
                Connection.Disposed += DisposedHandler;
                SqlCommand.Disposed += DisposedHandler;
            }


            /// <summary>
            /// An event handler that is fired if the SqlConnection or SqlCommand are disposed before this ConnectionAndCommand is disposed.
            /// </summary>
            private EventHandler DisposedHandler;

            /// <summary>
            /// Gets the SqlConnection
            /// </summary>
            public SqlConnection SqlConnection { get; private set; }
            /// <summary>
            /// Gets the SqlCommand that was created by this ConnectionAndCommand
            /// </summary>
            public SqlCommand SqlCommand { get; private set; }


            /// <summary>
            /// A boolean flag that stores whether or not we have already been disposed.
            /// </summary>
            private bool _isDisposed;
            /// <summary>
            /// An event that is called when the connection and command are disposed for the first time.
            /// </summary>
            public event Action Disposed;

            /// <summary>
            /// Disposes this object and its SqlConnection and SqlCommand.
            /// </summary>
            public void Dispose()
            {
                lock (this)
                {
                    if (!_isDisposed)
                    {
                        _isDisposed = true;
                    }
                    else
                    {
                        return;
                    }
                }
                SqlCommand.Disposed -= DisposedHandler;
                SqlConnection.Disposed -= DisposedHandler;

                SqlCommand.Dispose();
                SqlConnection.Dispose();

                Disposed?.Invoke();
            }
        }

        private static async Task<ConnectionAndCommand> CreateSqlCommandAsync(string CommandText, CommandType CommandType, bool RunAsynchronously)
        {
            if (ConnectionString == null)
            {
                throw new Exception("You must have a connection string with the name \"connection-string\" to use SqlUtil methods.");
            }

            var sql = new SqlConnection(ConnectionString);
            if (RunAsynchronously)
            {
                await sql.OpenAsync();
            }
            else
            {
                sql.Open();
            }

            return new ConnectionAndCommand(sql, CommandText, CommandType);
        }


        /// <summary>
        /// Creates and asynchronously opens a new SqlConnection. Then creates a SqlCommand on that connection.
        /// </summary>
        /// <param name="CommandText">The Command Text to set on the SqlCommand returned</param>
        /// <param name="CommandType">The type of the command. defaults to StoredProcedure.</param>
        /// <returns>A SqlCommand with the specified CommandText.</returns>
        public static Task<ConnectionAndCommand> CreateSqlCommandAsync(string CommandText = null, CommandType CommandType = CommandType.StoredProcedure)
        {
            return CreateSqlCommandAsync(CommandText, CommandType, true);
        }
        /// <summary>
        /// Creates and opens a new SqlConnection. Then creates a SqlCommand on that connection.
        /// </summary>
        /// <param name="CommandText">The Command Text to set on the SqlCommand returned</param>
        /// <param name="CommandType">The type of the command. defaults to StoredProcedure.</param>
        /// <returns>A SqlCommand with the specified CommandText.</returns>
        public static ConnectionAndCommand CreateSqlCommand(string CommandText = null, CommandType CommandType = CommandType.StoredProcedure)
        {
            return CreateSqlCommandAsync(CommandText, CommandType, false).Result;
        }
    }
}
