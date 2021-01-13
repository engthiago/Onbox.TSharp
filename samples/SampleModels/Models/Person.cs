﻿using System.Collections.Generic;

namespace SampleModels.Models
{
    public class Person
    {
        public string Name { get; set; }
        public string Company { get; set; }
        public string Company4 { get; set; }
        public SubPerson Related { get; set; }
        public SubPerson Related2 { get; set; }
        public List<SubPerson> Persons { get; set; }
        public SubPerson[] Persons2 { get; set; }
        public GenericPerson<SubPerson> Data { get; set; }
    }
}
