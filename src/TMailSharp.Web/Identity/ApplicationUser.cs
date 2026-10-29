using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TMailSharp.Identity;

public sealed class ApplicationUser : IdentityUser;

public sealed class IdentityDatabase(DbContextOptions<IdentityDatabase> options)
    : IdentityDbContext<ApplicationUser>(options);
