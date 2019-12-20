<Query Kind="Program">
  <Reference>&lt;RuntimeDirectory&gt;\System.Net.Http.dll</Reference>
  <NuGetReference>Microsoft.AspNet.WebApi.Client</NuGetReference>
  <NuGetReference>Microsoft.Azure.DocumentDB.Core</NuGetReference>
  <NuGetReference>NEST</NuGetReference>
  <NuGetReference>Newtonsoft.Json</NuGetReference>
  <Namespace>Elasticsearch.Net</Namespace>
  <Namespace>Nest</Namespace>
  <Namespace>Newtonsoft.Json</Namespace>
  <Namespace>Newtonsoft.Json.Bson</Namespace>
  <Namespace>Newtonsoft.Json.Converters</Namespace>
  <Namespace>Newtonsoft.Json.Linq</Namespace>
  <Namespace>Newtonsoft.Json.Schema</Namespace>
  <Namespace>Newtonsoft.Json.Serialization</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Net.Http.Formatting</Namespace>
  <Namespace>System.Net.Http.Handlers</Namespace>
  <Namespace>System.Net.Http.Headers</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
  <Namespace>Microsoft.Azure.Documents.Client</Namespace>
  <Namespace>Microsoft.Azure.Documents.Spatial</Namespace>
  <Namespace>Microsoft.Azure.Documents</Namespace>
  <Namespace>System.Xml.Serialization</Namespace>
</Query>

async Task Main()
{
	using (var httpClient = new HttpClient())
	{
		httpClient.BaseAddress = new Uri("https://vertica-xmas2019.azurewebsites.net");

		HttpResponseMessage apiResponse = await httpClient.PostAsJsonAsync("/api/participate", new { fullName = "", emailAddress = "", subscribeToNewsletter = true });

		if (!apiResponse.IsSuccessStatusCode)
			throw new InvalidOperationException($"{apiResponse.StatusCode}: {(await apiResponse.Content.ReadAsStringAsync())}");

		var participateResponse = await apiResponse.Content.ReadAsAsync<ParticipateResponse>();

		IElasticClient elasticClient = CreateElasticClient(participateResponse.Credentials.Username, participateResponse.Credentials.Password);

		GetResponse<SantaTracking> santaTrackingResponse = await elasticClient.GetAsync<SantaTracking>(participateResponse.Id, selector => selector.Index("santa-trackings"));

		if (!santaTrackingResponse.IsValid)
			throw new InvalidOperationException($"{santaTrackingResponse.DebugInformation}");

		GeoPoint santaPosition = santaTrackingResponse.Source.CalculateSantaPosition();

		apiResponse = await httpClient.PostAsJsonAsync("/api/santarescue", new { id = participateResponse.Id, position = santaPosition });

		if (!apiResponse.IsSuccessStatusCode)
			throw new InvalidOperationException($"{apiResponse.StatusCode}: {(await apiResponse.Content.ReadAsStringAsync())}");

		var santaRescueResponse = await apiResponse.Content.ReadAsAsync<SantaRescueResponse>();

		var locations = new List<ReindeerLocation>();

		using (var documentClient = new DocumentClient(new Uri("https://xmas2019.documents.azure.com"), santaRescueResponse.Token))
		{
			foreach (var zone in santaRescueResponse.Zones)
			{
				var center = new Point(zone.Center.Lon, zone.Center.Lat);
				double radiusInMeter = zone.Radius.InMeter();
				string name = zone.Reindeer.ToString();

				var feedOptions = new FeedOptions
				{
					PartitionKey = new PartitionKey(zone.CountryCode),
					MaxItemCount = 1
				};

				var actualReindeer = documentClient
					.CreateDocumentQuery<ObjectInWorld>(UriFactory.CreateDocumentCollectionUri("World", "Objects"), feedOptions)
					.Where(u => u.Name == name && center.Distance(u.Location) <= radiusInMeter)
					.AsEnumerable()
					.FirstOrDefault();

				var location = new ReindeerLocation
				{
					Name = zone.Reindeer,
					Position = new GeoPoint(actualReindeer.Location.Position.Latitude, actualReindeer.Location.Position.Longitude)
				};

				locations.Add(location);
			}
		}

		apiResponse = await httpClient.PostAsJsonAsync("/api/reindeerrescue", new { id = participateResponse.Id, locations = locations });

		if (!apiResponse.IsSuccessStatusCode)
			throw new InvalidOperationException($"{apiResponse.StatusCode}: {(await apiResponse.Content.ReadAsStringAsync())}");

		var reindeerRescueResponse = await apiResponse.Content.ReadAsAsync<ReindeerRescueResponse>();

		var toyDistributionXmlDocument = XDocument.Load(reindeerRescueResponse.ToyDistributionXmlUrl.ToString());
		var toyDistributionProblem = new XmlSerializer(typeof(ToyDistributionProblem)).Deserialize(toyDistributionXmlDocument.CreateReader()) as ToyDistributionProblem;
		
		apiResponse = await httpClient.PostAsJsonAsync("/api/toydistribution", new { id = participateResponse.Id, toyDistribution = toyDistributionProblem.CreateSolution() });

		if (!apiResponse.IsSuccessStatusCode)
			throw new InvalidOperationException($"{apiResponse.StatusCode}: {(await apiResponse.Content.ReadAsStringAsync())}");

		var toyDistributionResponse = await apiResponse.Content.ReadAsStringAsync();
		toyDistributionResponse.Dump();
	}
}

public class ReindeerRescueResponse
{
	public Uri ToyDistributionXmlUrl { get; set; }
}

public class ToyDistributionProblem
{
	public Toy[] Toys { get; set; }
	public Child[] Children { get; set; }
	
	public ToyDistributionSolution CreateSolution()
	{
		var solution = new ToyDistributionSolution(Toys);
		
		Dictionary<Toy, IEnumerable<Child>> toyMapsToChildren = Children
			.Where(solution.ChildNeedsGift)
			.SelectMany(x => x.WishList.Toys
				.Where(solution.ToyIsLeft), (child, toy) => new { child, toy })
			.GroupBy(x => x.toy, x => x.child)
			.ToDictionary(x => x.Key, x => x.AsEnumerable());

		while (solution.Remaining > 0)
		{
			Toy toy = toyMapsToChildren.First(x => 
			{
				var children = x.Value.Where(solution.ChildNeedsGift).ToArray();
				
				if (children.Length == 1)
				{
					solution.Add(children[0], x.Key);
					
					return true;
				}
				
				return false;
			}).Key;
			
			toyMapsToChildren.Remove(toy);
		}

		return solution;
	}
}

public class ToyDistributionSolution : IEnumerable<ToyDistributionSolution.ChildGetsToy>
{
	private readonly List<ChildGetsToy> _list;
	private readonly HashSet<Toy> _toysLeft;
	private readonly HashSet<Child> _childGotGift;
	
	public ToyDistributionSolution(Toy[] toys)
	{
		_list = new List<ToyDistributionSolution.ChildGetsToy>();

		_toysLeft = toys.ToHashSet();
		_childGotGift = new HashSet<Child>();
	}
	
	public bool ToyIsLeft(Toy toy)
	{
		return _toysLeft.Contains(toy);
	}
	
	public bool ChildNeedsGift(Child child)
	{
		return !ChildGotGift(child);
	}
	
	public bool ChildGotGift(Child child)
	{
		return _childGotGift.Contains(child);
	}
	
	public int Remaining => _toysLeft.Count;

	public void Add(Child child, Toy toy)
	{
		_list.Add(new ChildGetsToy { ChildName = child.Name, ToyName = toy.Name });

		_toysLeft.Remove(toy);
		_childGotGift.Add(child);
	}

	public IEnumerator<ChildGetsToy> GetEnumerator()
	{
		return ((IEnumerable<ChildGetsToy>)_list).GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return ((IEnumerable<ChildGetsToy>)_list).GetEnumerator();
	}

	public class ChildGetsToy
	{
		public string ChildName { get; set; }
		public string ToyName { get; set; }
	}
}

public class Child
{
	[XmlAttribute]
	public string Name { get; set; }
	public WishList WishList { get; set; }
}

public class WishList
{
	public Toy[] Toys { get; set; }
}

public class Toy : IEquatable<Toy>
{
	[XmlAttribute]
	public string Name { get; set; }

	public bool Equals(Toy other)
	{
		if (ReferenceEquals(null, other)) return false;
		if (ReferenceEquals(this, other)) return true;
		return Name == other.Name;
	}

	public override bool Equals(object obj)
	{
		if (ReferenceEquals(null, obj)) return false;
		if (ReferenceEquals(this, obj)) return true;
		if (obj.GetType() != GetType()) return false;
		return Equals((Toy)obj);
	}

	public override int GetHashCode()
	{
		return Name.GetHashCode();
	}
}

public class ObjectInWorld
{
	[JsonProperty("location")]
	public Point Location { get; set; }

	[JsonProperty("name")]
	public string Name { get; set; }
}

public class ReindeerLocation
{
	public string Name { get; set; }
	public GeoPoint Position { get; set; }
}

public class SantaTracking
{
	public GeoPoint CanePosition { get; set; }
	public Movement[] SantaMovements { get; set; }

	public GeoPoint CalculateSantaPosition()
	{
		double x = 0d;
		double y = 0d;

		foreach (Movement movement in SantaMovements)
		{
			double meters = movement.Unit.ToMeters(movement.Value);

			switch (movement.Direction)
			{
				case Direction.up:
					y += meters;
					break;
				case Direction.right:
					x += meters;
					break;
				case Direction.down:
					y -= meters;
					break;
				case Direction.left:
					x -= meters;
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		return CanePosition.MoveTo(x, y);
	}
}

public enum Direction
{
	up, 
	down, 
	left, 
	right
}

public class GeoPoint
{
	public GeoPoint(double lat, double lon)
	{
		Lat = lat;
		Lon = lon;
	}
	
	public double Lat { get; }
	public double Lon { get; }

	/// <summary>
	/// https://stackoverflow.com/questions/7477003/calculating-new-longitude-latitude-from-old-n-meters
	/// </summary>
	public GeoPoint MoveTo(double xMeters, double yMeters)
	{
		return new GeoPoint(
			MoveLatitude(Lat, yMeters),
			MoveLongitude(Lon, Lat, xMeters));

		double MoveLatitude(double latitude, double meters)
		{
			if (meters.Equals(0d))
				return latitude;

			return latitude + meters * M;
		}

		double MoveLongitude(double longitude, double latitude, double meters)
		{
			if (meters.Equals(0d))
				return longitude;

			return longitude + meters * M / Math.Cos(latitude * (Math.PI / 180));
		}
	}

	/// <summary>
	/// 1 meter in degree (with radius of the earth in kilometer)
	/// </summary>
	private const double M = 1 / (2 * Math.PI / 360 * 6378.137) / 1000;
}

public class Movement
{
	public Direction Direction { get; set; }
	public double Value { get; set; }
	public Unit Unit { get; set; }	
}

public class ParticipateResponse
{
	public Guid Id { get; set; }
	public Credentials Credentials { get; set; }
}

public class Credentials
{
	public string Username { get; set; }
	public string Password { get; set; }
}

private static IElasticClient CreateElasticClient(string username, string password)
{
	var settings = new ConnectionSettings("xmas2019:ZXUtY2VudHJhbC0xLmF3cy5jbG91ZC5lcy5pbyRlZWJjNmYyNzcxM2Q0NTE5OTcwZDc1Yzg2MDUwZTM2MyQyNDFmMzQ3OWNkNzg0ZTUyOTRkODk5OTViMjg0MjAyYg==",
		new BasicAuthenticationCredentials(username, password));
		
	return new ElasticClient(settings);
}

public class SantaRescueResponse
{
	public Zone[] Zones { get; set; }
	public string Token { get; set; }
}

public class Zone
{
	public string Reindeer { get; set; }
	public string CountryCode { get; set; }
	public string CityName { get; set; }
	public GeoPoint Center { get; set; }
	public Radius Radius { get; set; }
}

public class Radius
{
	public Unit Unit { get; set; }
	public double Value { get; set; }

	public double InMeter()
	{
		return Unit.ToMeters(Value);
	}
}

public static class Lengths
{
	public static double MetersToKilometers(this double meters)
	{
		return meters / 1000;
	}

	public static double KilometersToMeters(this double kilometers)
	{
		return kilometers * 1000;
	}

	public static double MetersToFeet(this double meters)
	{
		return meters / 0.304800610;
	}

	public static double FeetToMeters(this double feet)
	{
		return feet * 0.304800610;
	}

	public static double ToMeters(this Unit unit, double value)
	{
		switch (unit)
		{
			case Unit.foot:
				return value.FeetToMeters();

			case Unit.meter:
				return value;

			case Unit.kilometer:
				return value.KilometersToMeters();

			default:
				throw new ArgumentOutOfRangeException(nameof(unit), unit, null);
		}
	}

	public static double FromMeters(this double meters, Unit toUnit)
	{
		switch (toUnit)
		{
			case Unit.foot:
				return meters.MetersToFeet();
			case Unit.meter:
				return meters;
			case Unit.kilometer:
				return meters.MetersToKilometers();
			default:
				throw new ArgumentOutOfRangeException(nameof(toUnit), toUnit, null);
		}
	}
}

public enum Unit
{
	foot, 
	meter, 
	kilometer
}