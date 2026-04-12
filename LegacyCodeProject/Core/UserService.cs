namespace LegacyCodeProject.Core;

public class UserService
{
        public UserService()
        {
            // Some side-effect in the constructor that I want to avoid in tests
            Console.WriteLine("UserService created");
        }
    
        public string GetUserName(int userId)
        {
            // Imagine this method makes a database call or something else that we want to avoid in tests
            return $"User{userId}";
    }
}
