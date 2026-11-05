// Ryst = kast. Ansvarlig: Bjarke (S1.2, S1.3, S2.4, S3.3)
#pragma once

void shakeSetup();

// Returnerer true præcis én gang pr. ryst.
bool shakeDetected();

// True mens Oplaen bevæger sig (og lidt efter). Bruges til at ignorere touch under ryst.
// Opdateres af shakeDetected(), så den skal kaldes hver loop().
bool shakeMoving();

// Kort rasle-lyd fra buzzeren, som terninger i et bæger.
void shakeSound();
