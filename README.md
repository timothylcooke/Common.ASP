# Common.ASP

This project is the basis for all ASP sites. It provides a base `Controller` class (`MyController`) and an implementation of `MyJsonResult` that takes a `string` instead of an `object`.

It also provides SQL utility methods.

# Setup

Create a connection string with the name `connection-string` in your `web.config` file:

    <configuration>
      <connectionStrings>
        <add name="connection-string" connectionString="Data Source=.;Initial Catalog=????;Integrated Security=True;Connect Timeout=1000;Application Name=????.ASP" />
      </connectionStrings>
    </configuration>
    
A simple controller class may look something like this:

    public class MyApiController : MyController
    {
        [Route("api/v1/synchronize"), HttpPost]
        public Task<MyJsonResult> Synchronize()
        {
            return ExecuteSqlForJson("Synchronize", 
                    AddParameters: @params => @params.AddWithValue("ServerHostName", Request.Url.GetLeftPart(UriPartial.Authority))
                );
        }
    }

Assuming your connection string is correct, and you have permissions to call the specified stored procedures, the rest is magic.
