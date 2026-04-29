using LiveDeck.LicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddDbContext<LicenseDbContext>(opt =>
            opt.UseSqlServer(builder.Configuration.GetConnectionString("LicenseDb")));

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.MapControllers();

        app.Run();
    }
}
