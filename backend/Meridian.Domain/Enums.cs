namespace Meridian.Domain;

// Stored as text in the DB (readable), with a CHECK constraint on the allowed names.

public enum UserRole { Analyst, Admin }

public enum ImportStatus { Queued, Running, Completed, Failed }

// While Running: step 1 reads the file, step 2 classifies votes (progress counts per step)
public enum ImportStage { Reading, Classifying }

// ING-06: every row gets exactly one of these
public enum RowClassification { New, Unchanged, Duplicate, Invalid, Contradiction, Conflict }

// CON-03: a conflict is on a country's ballot or on one of the vote details
public enum ConflictField { Ballot, Title, Date, Session }

public enum ConflictStatus { Pending, Approved, Rejected }

public enum Stance { For, Against }

public enum SnapshotStatus { Queued, Running, Completed, Failed }

// Y = yes, N = no, A = abstain, X = absent
public enum Ballot { Y, N, A, X }

// Vote-level outcome of one import (ING-14 "what happened per vote")
public enum VoteDetailsOutcome { Created, Unchanged, Conflict, Contradiction, NotCreated }
