namespace Apps2Samsung.Models
{
    public class SamsungAuth
    {
        public string access_token { get; set; }
        public string token_type { get; set; }
        public string access_token_expires_in { get; set; }
        public string refresh_token { get; set; }
        public string refresh_token_expires_in { get; set; }
        public string userId { get; set; }
        public string client_id { get; set; }
        public string inputEmailID { get; set; }
        public string api_server_url { get; set; }
        public string auth_server_url { get; set; }
        public bool close { get; set; }
        public string closedAction { get; set; }
        public string state { get; set; }
    }
}
