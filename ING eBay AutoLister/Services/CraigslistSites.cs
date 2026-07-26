namespace ING_eBay_AutoLister.Services;

/// <summary>One craigslist regional site, and the ZIP-code prefixes it covers.</summary>
/// <param name="Id">The subdomain: <c>lasvegas</c> → lasvegas.craigslist.org.</param>
/// <param name="Label">How the site is named to the seller.</param>
/// <param name="State">Two-letter state, for the picker.</param>
/// <param name="Zip3">
/// Leading three digits of the ZIP codes this site serves. A few sites carry none — they exist so
/// they can be chosen explicitly, but no ZIP resolves to them because a bigger neighbour owns the
/// prefix (Yuma AZ and Glendale AZ share 853, and Phoenix is the far likelier search).
/// </param>
public sealed record CraigslistSite(string Id, string Label, string State, params int[] Zip3);

/// <summary>
/// Which craigslist site to search for a given ZIP code.
///
/// Craigslist is organised by metro area, not by radius: every post lives on exactly one regional
/// site, so a search has to start by picking one. Craigslist itself then does the real distance
/// filtering server-side from <c>postal</c> + <c>search_distance</c> (see CraigslistParser), which
/// means this only has to land on the right metro — being a town off doesn't skew results, it just
/// searches the wrong city's board.
///
/// Resolution is ZIP-prefix based and deliberately simple, because the alternative is shipping a
/// ZIP-code-to-coordinates database to answer a question craigslist is about to answer itself:
///   1. An explicitly chosen site always wins — the seller knows their own metro better than any
///      table, and the UI shows which site was picked so they can correct it.
///   2. Exact ZIP3 match against the table below.
///   3. Otherwise the numerically nearest ZIP3, which works because USPS assigned prefixes
///      geographically — 891 (Las Vegas) sits beside 890 (Henderson), not beside Maine.
///
/// Rule 3 is a genuine heuristic and can pick a neighbouring metro at a regional boundary. That's
/// why the chosen site is always reported back rather than applied silently.
/// </summary>
public static class CraigslistSites
{
    // Ordered so that where two metros share a prefix, the larger market is listed first: the
    // lookup below is first-registration-wins, and the bigger board is the better default.
    public static readonly CraigslistSite[] All =
    [
        // ── Northeast ────────────────────────────────────────────────────────────
        new("newyork",        "New York City",       "NY", 100, 101, 102, 103, 104, 110, 111, 112, 113, 114, 116),
        new("longisland",     "Long Island",         "NY", 115, 117, 118, 119),
        new("hudsonvalley",   "Hudson Valley",       "NY", 105, 106, 107, 108, 109, 124, 125, 126, 127, 128),
        new("albany",         "Albany",              "NY", 120, 121, 122, 123, 129),
        new("syracuse",       "Syracuse",            "NY", 130, 131, 132, 133, 134, 135, 136),
        new("rochester",      "Rochester",           "NY", 144, 145, 146, 147),
        new("buffalo",        "Buffalo",             "NY", 140, 141, 142, 143),
        new("binghamton",     "Binghamton",          "NY", 137, 138, 139),
        new("ithaca",         "Ithaca",              "NY", 148, 149),
        new("newjersey",      "North Jersey",        "NJ", 70, 71, 72, 73, 74, 75, 76, 79),
        new("cnj",            "Central NJ",          "NJ", 77, 78, 85, 88, 89),
        new("southjersey",    "South Jersey",        "NJ", 80, 81, 82, 83, 84),
        new("jerseyshore",    "Jersey Shore",        "NJ", 87),
        new("philadelphia",   "Philadelphia",        "PA", 190, 191, 192, 193, 194),
        new("pittsburgh",     "Pittsburgh",          "PA", 150, 151, 152, 153, 154, 155, 156, 157, 158, 159, 160, 161, 162, 163),
        new("harrisburg",     "Harrisburg",          "PA", 170, 171, 172, 173, 174),
        new("allentown",      "Allentown",           "PA", 180, 181, 182, 183),
        new("scranton",       "Scranton",            "PA", 184, 185, 186, 187, 188, 189),
        new("lancaster",      "Lancaster",           "PA", 175, 176),
        new("reading",        "Reading",             "PA", 195, 196),
        new("erie",           "Erie",                "PA", 164, 165),
        new("altoona",        "Altoona",             "PA", 166, 167),
        new("statecollege",   "State College",       "PA", 168, 169),
        new("williamsport",   "Williamsport",        "PA", 177, 178, 179),
        new("boston",         "Boston",              "MA", 17, 18, 19, 20, 21, 22, 23, 24),
        new("worcester",      "Worcester",           "MA", 14, 15, 16),
        new("westernmass",    "Western Massachusetts","MA", 10, 11, 12, 13),
        new("capecod",        "Cape Cod",            "MA", 25, 26, 27),
        new("rhodeisland",    "Rhode Island",        "RI", 28, 29),
        new("newhampshire",   "New Hampshire",       "NH", 30, 31, 32, 33, 34, 35, 36, 37, 38),
        new("maine",          "Maine",               "ME", 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49),
        new("vermont",        "Vermont",             "VT", 50, 51, 52, 53, 54, 55, 56, 57, 58, 59),
        new("hartford",       "Hartford",            "CT", 60, 61, 62),
        new("newhaven",       "New Haven",           "CT", 63, 64, 65, 66, 67),

        // ── Mid-Atlantic ─────────────────────────────────────────────────────────
        new("washingtondc",   "Washington DC",       "DC", 200, 201, 202, 203, 204, 205, 206, 207, 208, 209, 220, 221, 222, 223),
        new("baltimore",      "Baltimore",           "MD", 210, 211, 212, 214, 215, 216, 217, 218, 219),
        new("delaware",       "Delaware",            "DE", 197, 198, 199),
        new("richmond",       "Richmond",            "VA", 230, 231, 232, 238),
        new("norfolk",        "Hampton Roads",       "VA", 233, 234, 235, 236, 237, 239),
        new("roanoke",        "Roanoke",             "VA", 240, 241, 242, 243, 244),
        new("lynchburg",      "Lynchburg",           "VA", 245, 246),
        new("charlottesville","Charlottesville",     "VA", 229),
        new("harrisonburg",   "Harrisonburg",        "VA", 228),
        new("winchester",     "Winchester",          "VA", 226, 227),
        new("fredericksburg", "Fredericksburg",      "VA", 224, 225),
        new("morgantown",     "Morgantown",          "WV", 265, 266, 267, 268),
        new("huntington",     "Huntington-Ashland",  "WV", 255, 256, 257, 258, 259),
        new("wheeling",       "Wheeling",            "WV", 260, 262, 263),
        new("parkersburg",    "Parkersburg",         "WV", 261, 264),

        // ── Southeast ────────────────────────────────────────────────────────────
        new("raleigh",        "Raleigh-Durham",      "NC", 275, 276, 277),
        new("charlotte",      "Charlotte",           "NC", 280, 281, 282),
        new("greensboro",     "Greensboro",          "NC", 272, 273, 274),
        new("winstonsalem",   "Winston-Salem",       "NC", 270, 271),
        new("asheville",      "Asheville",           "NC", 287, 288, 289),
        new("fayetteville",   "Fayetteville",        "NC", 283),
        new("wilmington",     "Wilmington",          "NC", 284, 285),
        new("hickory",        "Hickory",             "NC", 286),
        new("eastnc",         "Eastern NC",          "NC", 278, 279),
        new("charleston",     "Charleston",          "SC", 294, 299),
        new("columbia",       "Columbia",            "SC", 290, 291, 292, 293),
        new("greenville",     "Greenville",          "SC", 296, 297, 298),
        new("myrtlebeach",    "Myrtle Beach",        "SC", 295),
        new("atlanta",        "Atlanta",             "GA", 300, 301, 302, 303, 305, 307, 311),
        new("athensga",       "Athens",              "GA", 306),
        new("augusta",        "Augusta",             "GA", 308, 309),
        new("macon",          "Macon",               "GA", 310, 312),
        new("savannah",       "Savannah",            "GA", 313, 314, 315),
        new("valdosta",       "Valdosta",            "GA", 316),
        new("albanyga",       "Albany",              "GA", 317),
        new("columbusga",     "Columbus",            "GA", 318, 319),
        new("jacksonville",   "Jacksonville",        "FL", 320, 322),
        new("daytona",        "Daytona Beach",       "FL", 321),
        new("orlando",        "Orlando",             "FL", 327, 328, 347),
        new("spacecoast",     "Space Coast",         "FL", 329),
        new("tampa",          "Tampa Bay",           "FL", 335, 336, 337, 346),
        new("miami",          "Miami / Fort Lauderdale / West Palm", "FL", 330, 331, 332, 333, 334),
        new("fortmyers",      "Fort Myers",          "FL", 339, 341),
        new("sarasota",       "Sarasota-Bradenton",  "FL", 342, 343),
        new("lakeland",       "Lakeland",            "FL", 338),
        new("ocala",          "Ocala",               "FL", 344),
        new("gainesville",    "Gainesville",         "FL", 326),
        new("tallahassee",    "Tallahassee",         "FL", 323),
        new("panamacity",     "Panama City",         "FL", 324),
        new("pensacola",      "Pensacola",           "FL", 325),
        new("treasure",       "Treasure Coast",      "FL", 349),
        new("keys",           "Florida Keys",        "FL", 340),
        new("bham",           "Birmingham",          "AL", 350, 351, 352, 353, 354, 355, 356),
        new("huntsville",     "Huntsville",          "AL", 357, 358, 359),
        new("montgomery",     "Montgomery",          "AL", 360, 361, 362, 363, 364, 367),
        new("mobile",         "Mobile",              "AL", 365, 366, 368, 369),
        new("jackson",        "Jackson",             "MS", 390, 391, 392, 393),
        new("hattiesburg",    "Hattiesburg",         "MS", 394),
        new("gulfport",       "Gulfport-Biloxi",     "MS", 395),
        new("memphis",        "Memphis",             "TN", 380, 381, 382, 383, 384, 385, 386),
        new("nashville",      "Nashville",           "TN", 370, 371, 372, 373),
        new("chattanooga",    "Chattanooga",         "TN", 374),
        new("knoxville",      "Knoxville",           "TN", 377, 378, 379),
        new("tricities",      "Tri-Cities",          "TN", 375, 376),
        new("louisville",     "Louisville",          "KY", 400, 401, 402),
        new("lexington",      "Lexington",           "KY", 403, 404, 405, 406, 407, 408, 409),
        new("eastky",         "Eastern Kentucky",    "KY", 410, 411, 412, 413, 414, 415, 416, 417, 418),
        new("bowlinggreen",   "Bowling Green",       "KY", 421, 422),
        new("owensboro",      "Owensboro",           "KY", 423, 424, 425, 426, 427),

        // ── Midwest ──────────────────────────────────────────────────────────────
        new("chicago",        "Chicago",             "IL", 600, 601, 602, 603, 604, 605, 606, 607, 608),
        new("rockford",       "Rockford",            "IL", 610, 611),
        new("quadcities",     "Quad Cities",         "IL", 612, 527, 528),
        new("peoria",         "Peoria",              "IL", 615, 616),
        new("chambana",       "Champaign-Urbana",    "IL", 618, 619),
        new("springfieldil",  "Springfield",         "IL", 625, 626, 627),
        new("carbondale",     "Southern Illinois",   "IL", 628, 629),
        new("stlouis",        "St Louis",            "MO", 620, 621, 622, 623, 624, 630, 631, 633),
        new("semo",           "Southeast Missouri",  "MO", 636, 637, 638, 639),
        new("columbiamo",     "Columbia / Jeff City","MO", 650, 651, 652, 653),
        new("kansascity",     "Kansas City",         "MO", 640, 641, 644, 661, 662, 664, 665, 667, 669),
        new("stjoseph",       "St Joseph",           "MO", 645, 646, 647),
        new("joplin",         "Joplin",              "MO", 648, 649),
        new("springfield",    "Springfield",         "MO", 654, 655, 656, 657, 658),
        new("lawrence",       "Lawrence",            "KS", 660),
        new("topeka",         "Topeka",              "KS", 663, 666),
        new("wichita",        "Wichita",             "KS", 670, 671, 672, 673),
        new("salina",         "Salina",              "KS", 674),
        new("swks",           "Southwest Kansas",    "KS", 675, 676, 677, 678, 679),
        new("desmoines",      "Des Moines",          "IA", 501, 502, 503),
        new("ames",           "Ames",                "IA", 500),
        new("fortdodge",      "Fort Dodge",          "IA", 505),
        new("waterloo",       "Waterloo / Cedar Falls","IA", 506, 507),
        new("siouxcity",      "Sioux City",          "IA", 510, 511),
        new("masoncity",      "Mason City",          "IA", 504),
        new("dubuque",        "Dubuque",             "IA", 520),
        new("iowacity",       "Iowa City",           "IA", 522),
        new("cedarrapids",    "Cedar Rapids",        "IA", 523, 524, 526),
        new("ottumwa",        "Ottumwa",             "IA", 525),
        new("omaha",          "Omaha / Council Bluffs","NE", 515, 516, 680, 681, 682, 683),
        new("lincoln",        "Lincoln",             "NE", 684, 685),
        new("grandisland",    "Grand Island",        "NE", 686, 687, 688),
        new("northplatte",    "North Platte",        "NE", 690, 691),
        new("scottsbluff",    "Scottsbluff",         "NE", 693),
        new("minneapolis",    "Minneapolis / St Paul","MN", 550, 551, 552, 553, 554, 555, 556),
        new("duluth",         "Duluth / Superior",   "MN", 557, 558),
        new("rmn",            "Rochester",           "MN", 559),
        new("mankato",        "Mankato",             "MN", 560, 561),
        new("stcloud",        "St Cloud",            "MN", 562, 563, 564),
        new("bemidji",        "Bemidji",             "MN", 565, 566, 567),
        new("fargo",          "Fargo / Moorhead",    "ND", 580, 581, 583, 584),
        new("grandforks",     "Grand Forks",         "ND", 582),
        new("bismarck",       "Bismarck",            "ND", 585, 586, 587, 588),
        new("siouxfalls",     "Sioux Falls",         "SD", 570, 571, 572, 573, 574),
        new("rapidcity",      "Rapid City",          "SD", 575, 576, 577),
        new("milwaukee",      "Milwaukee",           "WI", 530, 531, 532),
        new("racine",         "Racine",              "WI", 534),
        new("janesville",     "Janesville",          "WI", 533, 535),
        new("madison",        "Madison",             "WI", 537, 539),
        new("lacrosse",       "La Crosse",           "WI", 536, 546),
        new("eauclaire",      "Eau Claire",          "WI", 547, 548),
        new("wausau",         "Wausau",              "WI", 544, 545),
        new("greenbay",       "Green Bay",           "WI", 541, 542, 543),
        new("appleton",       "Appleton / Oshkosh",  "WI", 549),
        new("detroit",        "Detroit Metro",       "MI", 480, 482, 483),
        new("annarbor",       "Ann Arbor",           "MI", 481),
        new("flint",          "Flint",               "MI", 484, 485),
        new("saginaw",        "Saginaw / Bay City",  "MI", 486, 487),
        new("lansing",        "Lansing",             "MI", 488, 489),
        new("kalamazoo",      "Kalamazoo",           "MI", 490, 491, 492),
        new("grandrapids",    "Grand Rapids",        "MI", 493, 494, 495),
        new("nmi",            "Northern Michigan",   "MI", 496, 497),
        new("up",             "Upper Peninsula",     "MI", 498, 499),
        new("indianapolis",   "Indianapolis",        "IN", 460, 461, 462, 470, 471, 472),
        new("nwi",            "Northwest Indiana",   "IN", 463, 464),
        new("southbend",      "South Bend / Michiana","IN", 465, 466),
        new("fortwayne",      "Fort Wayne",          "IN", 467, 468),
        new("kokomo",         "Kokomo",              "IN", 469),
        new("muncie",         "Muncie / Anderson",   "IN", 473),
        new("bloomington",    "Bloomington",         "IN", 474, 475),
        new("evansville",     "Evansville",          "IN", 476, 477),
        new("terrehaute",     "Terre Haute",         "IN", 478),
        new("tippecanoe",     "Lafayette / West Lafayette","IN", 479),
        new("columbus",       "Columbus",            "OH", 430, 431, 432, 433),
        new("cleveland",      "Cleveland",           "OH", 440, 441, 442),
        new("akroncanton",    "Akron / Canton",      "OH", 443, 446, 447),
        new("youngstown",     "Youngstown",          "OH", 444, 445),
        new("mansfield",      "Mansfield",           "OH", 448, 449),
        new("cincinnati",     "Cincinnati",          "OH", 450, 451, 452, 459),
        new("dayton",         "Dayton / Springfield","OH", 453, 454, 455),
        new("chillicothe",    "Chillicothe",         "OH", 456, 457),
        new("limaohio",       "Lima / Findlay",      "OH", 458),
        new("toledo",         "Toledo",              "OH", 434, 435, 436),
        new("zanesville",     "Zanesville / Cambridge","OH", 437, 438, 439),

        // ── South Central ────────────────────────────────────────────────────────
        new("neworleans",     "New Orleans",         "LA", 700, 701, 702, 703, 704),
        new("batonrouge",     "Baton Rouge",         "LA", 707, 708, 709),
        new("lafayette",      "Lafayette",           "LA", 705),
        new("lakecharles",    "Lake Charles",        "LA", 706),
        new("shreveport",     "Shreveport",          "LA", 710, 711),
        new("monroe",         "Monroe",              "LA", 712, 713, 714),
        new("littlerock",     "Little Rock",         "AR", 715, 716, 720, 721, 722, 723),
        new("jonesboro",      "Jonesboro",           "AR", 724),
        new("fayar",          "Fayetteville / NW Arkansas","AR", 726, 727),
        new("fortsmith",      "Fort Smith",          "AR", 728, 729),
        new("hotsprings",     "Hot Springs",         "AR", 719),
        new("texarkana",      "Texarkana",           "AR", 717, 718, 755),
        new("oklahomacity",   "Oklahoma City",       "OK", 730, 731, 734, 736, 737, 738, 739),
        new("tulsa",          "Tulsa",               "OK", 740, 741, 743, 744, 745, 746, 747, 748, 749),
        new("lawton",         "Lawton",              "OK", 735),
        new("dallas",         "Dallas / Fort Worth", "TX", 750, 751, 752, 753, 754, 756, 757, 758, 759, 760, 761, 762, 764),
        new("wichitafalls",   "Wichita Falls",       "TX", 763),
        new("waco",           "Waco",                "TX", 765, 766, 767, 768),
        new("sanangelo",      "San Angelo",          "TX", 769),
        new("houston",        "Houston",             "TX", 770, 771, 772, 773, 774),
        new("galveston",      "Galveston",           "TX", 775),
        new("beaumont",       "Beaumont / Port Arthur","TX", 776, 777),
        new("collegestation", "College Station",     "TX", 778),
        new("victoriatx",     "Victoria",            "TX", 779),
        new("laredo",         "Laredo",              "TX", 780),
        new("sanantonio",     "San Antonio",         "TX", 781, 782),
        new("corpuschristi",  "Corpus Christi",      "TX", 783, 784),
        new("rgv",            "Rio Grande Valley",   "TX", 785),
        new("austin",         "Austin",              "TX", 786, 787, 788, 789),
        new("amarillo",       "Amarillo",            "TX", 790, 791),
        new("lubbock",        "Lubbock",             "TX", 792, 793, 794),
        new("abilene",        "Abilene",             "TX", 795, 796),
        new("odessa",         "Midland / Odessa",    "TX", 797),
        new("elpaso",         "El Paso",             "TX", 798, 799, 885),

        // ── Mountain ─────────────────────────────────────────────────────────────
        new("denver",         "Denver",              "CO", 800, 801, 802),
        new("boulder",        "Boulder",             "CO", 803, 804),
        new("fortcollins",    "Fort Collins",        "CO", 805, 806),
        new("eastco",         "Eastern Colorado",    "CO", 807),
        new("coloradosprings","Colorado Springs",    "CO", 808, 809),
        new("pueblo",         "Pueblo",              "CO", 810, 811),
        new("rockies",        "High Rockies",        "CO", 812, 813, 814),
        new("westernslope",   "Western Slope",       "CO", 815, 816),
        new("wyoming",        "Wyoming",             "WY", 820, 821, 822, 823, 824, 825, 826, 827, 828, 829, 830, 831),
        new("eastidaho",      "East Idaho",          "ID", 832, 834),
        new("twinfalls",      "Twin Falls",          "ID", 833),
        new("lewiston",       "Lewiston / Clarkston","ID", 835),
        new("boise",          "Boise",               "ID", 836, 837, 838),
        new("saltlakecity",   "Salt Lake City",      "UT", 840, 841),
        new("logan",          "Logan",               "UT", 843),
        new("ogden",          "Ogden / Clearfield",  "UT", 842, 844),
        new("provo",          "Provo / Orem",        "UT", 845, 846),
        new("stgeorge",       "St George",           "UT", 847),
        new("lasvegas",       "Las Vegas",           "NV", 889, 890, 891),
        new("reno",           "Reno / Tahoe",        "NV", 894, 895, 897),
        new("elko",           "Elko",                "NV", 893, 898),
        new("albuquerque",    "Albuquerque",         "NM", 870, 871, 872, 873),
        new("farmington",     "Farmington",          "NM", 874),
        new("santafe",        "Santa Fe / Taos",     "NM", 875, 877),
        new("lascruces",      "Las Cruces",          "NM", 879, 880),
        new("roswell",        "Roswell / Carlsbad",  "NM", 881, 882, 883, 884),
        new("phoenix",        "Phoenix",             "AZ", 850, 851, 852, 853),
        new("tucson",         "Tucson",              "AZ", 855, 856, 857),
        new("showlow",        "Show Low",            "AZ", 859),
        new("flagstaff",      "Flagstaff / Sedona",  "AZ", 860),
        new("mohave",         "Mohave County",       "AZ", 864),
        new("prescott",       "Prescott",            "AZ", 863),
        new("yuma",           "Yuma",                "AZ"),  // 853 is Glendale AZ first — pick Yuma explicitly
        new("billings",       "Billings",            "MT", 590, 591, 592),
        new("greatfalls",     "Great Falls",         "MT", 594, 595),
        new("helena",         "Helena",              "MT", 596),
        new("bozeman",        "Bozeman",             "MT", 597),
        new("missoula",       "Missoula",            "MT", 593, 598),
        new("kalispell",      "Kalispell",           "MT", 599),

        // ── West Coast ───────────────────────────────────────────────────────────
        new("seattle",        "Seattle / Tacoma",    "WA", 980, 981, 983, 984),
        new("bellingham",     "Bellingham",          "WA", 982),
        new("olympia",        "Olympia / Thurston",  "WA", 985),
        new("skagit",         "Skagit / Island",     "WA", 986),
        new("wenatchee",      "Wenatchee",           "WA", 988),
        new("yakima",         "Yakima",              "WA", 989),
        new("spokane",        "Spokane / Coeur d'Alene","WA", 990, 991, 992),
        new("kpr",            "Tri-Cities",          "WA", 993),
        new("pullman",        "Pullman / Moscow",    "WA", 994),
        new("moseslake",      "Moses Lake",          "WA", 987),
        new("portland",       "Portland",            "OR", 970, 971, 972),
        new("salem",          "Salem",               "OR", 973),
        new("eugene",         "Eugene",              "OR", 974),
        new("medford",        "Medford / Ashland",   "OR", 975),
        new("klamath",        "Klamath Falls",       "OR", 976),
        new("bend",           "Bend",                "OR", 977),
        new("eastoregon",     "East Oregon",         "OR", 978, 979),
        new("losangeles",     "Los Angeles",         "CA", 900, 901, 902, 903, 904, 905, 906, 907, 908, 910, 911, 912, 913, 914, 915, 916, 917, 918, 935),
        new("sandiego",       "San Diego",           "CA", 919, 920, 921),
        new("palmsprings",    "Palm Springs",        "CA", 922),
        new("inlandempire",   "Inland Empire",       "CA", 923, 924, 925),
        new("orangecounty",   "Orange County",       "CA", 926, 927, 928),
        new("ventura",        "Ventura County",      "CA", 930),
        new("santabarbara",   "Santa Barbara",       "CA", 931),
        new("visalia",        "Visalia / Tulare",    "CA", 932),
        new("bakersfield",    "Bakersfield",         "CA", 933),
        new("slo",            "San Luis Obispo",     "CA", 934),
        new("fresno",         "Fresno / Madera",     "CA", 936, 937, 938),
        new("monterey",       "Monterey Bay",        "CA", 939),
        new("sfbay",          "SF Bay Area",         "CA", 940, 941, 943, 944, 945, 946, 947, 948, 949, 950, 951, 954),
        new("stockton",       "Stockton",            "CA", 952),
        new("modesto",        "Modesto / Merced",    "CA", 953),
        new("humboldt",       "Humboldt County",     "CA", 955),
        new("sacramento",     "Sacramento",          "CA", 942, 956, 957, 958),
        new("chico",          "Chico",               "CA", 959),
        new("redding",        "Redding",             "CA", 960, 961),
        new("honolulu",       "Hawaii",              "HI", 967, 968),
        new("anchorage",      "Anchorage",           "AK", 995, 996, 999),
        new("fairbanks",      "Fairbanks",           "AK", 997),
        new("juneau",         "Juneau / Southeast Alaska","AK", 998),
    ];

    // First registration wins where two metros share a prefix — see the ordering note above.
    private static readonly Dictionary<int, CraigslistSite> ByZip3 = BuildZipIndex();

    private static Dictionary<int, CraigslistSite> BuildZipIndex()
    {
        var map = new Dictionary<int, CraigslistSite>();
        foreach (var site in All)
            foreach (var zip3 in site.Zip3)
                map.TryAdd(zip3, site);
        return map;
    }

    public static CraigslistSite? ById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(s => s.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The leading three digits of a US ZIP, or null for anything that isn't one — a Canadian
    /// postal code or a typo has no craigslist metro, and guessing at one would search a random
    /// city rather than admit it.
    /// </summary>
    public static int? Zip3Of(string? zip)
    {
        var digits = new string((zip ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return null;
        return int.Parse(digits[..3]);
    }

    /// <summary>
    /// Picks the site to search: an explicit choice first, then the ZIP's own metro, then the
    /// numerically nearest ZIP3 (see the class remarks for why that works and where it doesn't).
    /// Returns null only when there's nothing to go on at all.
    /// </summary>
    public static CraigslistSite? Resolve(string? zip, string? explicitSiteId = null)
    {
        var chosen = ById(explicitSiteId);
        if (chosen is not null) return chosen;

        var zip3 = Zip3Of(zip);
        if (zip3 is null) return null;
        if (ByZip3.TryGetValue(zip3.Value, out var exact)) return exact;

        return ByZip3
            .OrderBy(kv => Math.Abs(kv.Key - zip3.Value))
            .ThenBy(kv => kv.Value.Id, StringComparer.Ordinal)
            .Select(kv => kv.Value)
            .FirstOrDefault();
    }

    /// <summary>True when the ZIP landed on its own metro rather than on a numeric neighbour.</summary>
    public static bool IsExactZipMatch(string? zip)
    {
        var zip3 = Zip3Of(zip);
        return zip3 is not null && ByZip3.ContainsKey(zip3.Value);
    }
}
